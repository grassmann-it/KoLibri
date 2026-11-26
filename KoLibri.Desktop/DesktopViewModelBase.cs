using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace KoLibri.Desktop;

/// <summary>Lifecycle shown by a desktop read model.</summary>
public enum DesktopLoadState
{
    Idle,
    Loading,
    Ready,
    Empty,
    Stale,
    Canceled,
    Error
}

/// <summary>Small, UI-independent observable base used by all desktop projections.</summary>
public abstract class DesktopViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly object _loadGate = new();
    private CancellationTokenSource? _loadCancellation;
    private long _loadGeneration;
    private DesktopLoadState _loadState = DesktopLoadState.Idle;
    private bool _isStale;
    private string? _errorMessage;
    private string? _errorCode;
    private DateTimeOffset? _lastUpdatedAt;
    private DesktopLoadState _operationState = DesktopLoadState.Idle;
    private string? _operationErrorMessage;
    private CancellationTokenSource? _operationCancellation;
    private long _operationGeneration;
    private bool _isDisposed;

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Current lifecycle state. Loading never clears the last good projection.</summary>
    public DesktopLoadState LoadState
    {
        get => _loadState;
        protected set
        {
            if (!SetProperty(ref _loadState, value)) return;
            OnPropertyChanged(nameof(Status));
            OnPropertyChanged(nameof(IsLoading));
            OnPropertyChanged(nameof(IsBusy));
            OnPropertyChanged(nameof(HasError));
            OnPropertyChanged(nameof(IsCanceled));
        }
    }

    /// <summary>Alias that is convenient for Avalonia bindings.</summary>
    public DesktopLoadState Status => LoadState;

    public bool IsLoading => LoadState == DesktopLoadState.Loading;
    public bool IsBusy => IsLoading;
    public bool IsStale
    {
        get => _isStale;
        protected set => SetProperty(ref _isStale, value);
    }

    public bool HasError => LoadState == DesktopLoadState.Error;
    public bool IsCanceled => LoadState == DesktopLoadState.Canceled;
    public string? ErrorMessage
    {
        get => _errorMessage;
        protected set
        {
            if (SetProperty(ref _errorMessage, value))
                OnPropertyChanged(nameof(HasError));
        }
    }

    public string? ErrorCode
    {
        get => _errorCode;
        protected set => SetProperty(ref _errorCode, value);
    }

    public DateTimeOffset? LastUpdatedAt
    {
        get => _lastUpdatedAt;
        protected set => SetProperty(ref _lastUpdatedAt, value);
    }

    /// <summary>Lifecycle for a preview/approval action independent of the read state.</summary>
    public DesktopLoadState OperationState
    {
        get => _operationState;
        protected set
        {
            if (!SetProperty(ref _operationState, value)) return;
            OnPropertyChanged(nameof(IsOperationBusy));
            OnPropertyChanged(nameof(OperationHasError));
        }
    }

    public bool IsOperationBusy => OperationState == DesktopLoadState.Loading;
    public bool OperationHasError => OperationState == DesktopLoadState.Error;
    public string? OperationErrorMessage
    {
        get => _operationErrorMessage;
        protected set => SetProperty(ref _operationErrorMessage, value);
    }

    /// <summary>Starts a read and makes a previous read obsolete.</summary>
    protected async Task<T?> LoadCoreAsync<T>(
        Func<CancellationToken, Task<T>> loader,
        Action<T> apply,
        Func<T, DesktopLoadState>? stateSelector = null,
        Func<T, bool>? staleSelector = null,
        Func<T, DateTimeOffset?>? observedAtSelector = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(loader);
        ArgumentNullException.ThrowIfNull(apply);
        ThrowIfDisposed();

        var (generation, linked) = BeginLoad(cancellationToken);
        try
        {
            var value = await loader(linked.Token).ConfigureAwait(false);
            if (!IsCurrent(generation)) return value;
            if (linked.IsCancellationRequested || cancellationToken.IsCancellationRequested)
            {
                LoadState = DesktopLoadState.Canceled;
                return default;
            }

            apply(value);
            IsStale = staleSelector?.Invoke(value) ?? false;
            LastUpdatedAt = observedAtSelector?.Invoke(value) ?? DateTimeOffset.UtcNow;
            var nextState = stateSelector?.Invoke(value) ?? (IsStale ? DesktopLoadState.Stale : DesktopLoadState.Ready);
            if (nextState != DesktopLoadState.Error)
            {
                ErrorMessage = null;
                ErrorCode = null;
            }
            else if (string.IsNullOrWhiteSpace(ErrorMessage))
            {
                ErrorMessage = "The query reported an error.";
            }
            LoadState = nextState;
            return value;
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested || cancellationToken.IsCancellationRequested)
        {
            if (IsCurrent(generation))
            {
                LoadState = DesktopLoadState.Canceled;
                OnPropertyChanged(nameof(IsCanceled));
            }
            return default;
        }
        catch (Exception exception)
        {
            if (IsCurrent(generation))
            {
                ErrorMessage = exception.Message;
                ErrorCode = exception.GetType().Name;
                LoadState = DesktopLoadState.Error;
                OnPropertyChanged(nameof(HasError));
            }
            return default;
        }
        finally
        {
            EndLoad(generation, linked);
        }
    }

    /// <summary>Runs an approval/preview operation without replacing read-side data.</summary>
    protected async Task<T?> OperationCoreAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        Action<T> apply,
        CancellationToken cancellationToken = default,
        Func<T, DesktopLoadState>? stateSelector = null)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(apply);
        ThrowIfDisposed();
        var (generation, linked) = BeginOperation(cancellationToken);
        try
        {
            var value = await operation(linked.Token).ConfigureAwait(false);
            if (!IsCurrentOperation(generation)) return value;
            if (linked.IsCancellationRequested || cancellationToken.IsCancellationRequested)
            {
                OperationState = DesktopLoadState.Canceled;
                return default;
            }
            apply(value);
            var nextState = stateSelector?.Invoke(value) ?? DesktopLoadState.Ready;
            OperationErrorMessage = nextState == DesktopLoadState.Error
                ? "The operation reported an error."
                : null;
            OperationState = nextState;
            return value;
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested || cancellationToken.IsCancellationRequested)
        {
            if (IsCurrentOperation(generation)) OperationState = DesktopLoadState.Canceled;
            return default;
        }
        catch (Exception exception)
        {
            if (IsCurrentOperation(generation))
            {
                OperationErrorMessage = exception.Message;
                OperationState = DesktopLoadState.Error;
            }
            return default;
        }
        finally
        {
            EndOperation(generation, linked);
        }
    }

    /// <summary>Cancel only the currently active read; existing data remains visible.</summary>
    public void Cancel()
    {
        CancellationTokenSource? cancellation;
        lock (_loadGate)
        {
            cancellation = _loadCancellation;
            _loadCancellation = null;
            _loadGeneration++;
        }

        cancellation?.Cancel();
        cancellation?.Dispose();
        if (_isDisposed) return;
        LoadState = DesktopLoadState.Canceled;
        OnPropertyChanged(nameof(IsCanceled));
    }

    public void CancelOperation()
    {
        CancellationTokenSource? cancellation;
        lock (_loadGate)
        {
            cancellation = _operationCancellation;
            _operationCancellation = null;
            _operationGeneration++;
        }
        cancellation?.Cancel();
        cancellation?.Dispose();
        if (!_isDisposed) OperationState = DesktopLoadState.Canceled;
    }

    protected void SetLoadedState(DesktopLoadState state, bool stale, DateTimeOffset? observedAt = null)
    {
        IsStale = stale;
        LastUpdatedAt = observedAt ?? DateTimeOffset.UtcNow;
        LoadState = state;
    }

    protected static DesktopLoadState StateFor(GatewayState state, bool hasData = false)
        => state switch
        {
            GatewayState.Loading => DesktopLoadState.Loading,
            GatewayState.Empty => DesktopLoadState.Empty,
            GatewayState.Stale => DesktopLoadState.Stale,
            GatewayState.Error => DesktopLoadState.Error,
            GatewayState.Ready or GatewayState.Approved or GatewayState.Committed or GatewayState.Verified
                => hasData ? DesktopLoadState.Ready : DesktopLoadState.Empty,
            _ => hasData ? DesktopLoadState.Ready : DesktopLoadState.Empty
        };

    protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    protected void ThrowIfDisposed()
    {
        if (_isDisposed) throw new ObjectDisposedException(GetType().Name);
    }

    private (long Generation, CancellationTokenSource Cancellation) BeginLoad(CancellationToken cancellationToken)
    {
        lock (_loadGate)
        {
            _loadCancellation?.Cancel();
            _loadCancellation?.Dispose();
            var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _loadCancellation = linked;
            var generation = ++_loadGeneration;
            ErrorMessage = null;
            ErrorCode = null;
            LoadState = DesktopLoadState.Loading;
            OnPropertyChanged(nameof(IsLoading));
            OnPropertyChanged(nameof(IsBusy));
            OnPropertyChanged(nameof(HasError));
            OnPropertyChanged(nameof(IsCanceled));
            return (generation, linked);
        }
    }

    private bool IsCurrent(long generation)
    {
        lock (_loadGate) return generation == _loadGeneration && !_isDisposed;
    }

    private void EndLoad(long generation, CancellationTokenSource cancellation)
    {
        lock (_loadGate)
        {
            if (generation == _loadGeneration && ReferenceEquals(_loadCancellation, cancellation))
                _loadCancellation = null;
        }
        cancellation.Dispose();
        OnPropertyChanged(nameof(IsLoading));
        OnPropertyChanged(nameof(IsBusy));
    }

    private (long Generation, CancellationTokenSource Cancellation) BeginOperation(CancellationToken cancellationToken)
    {
        lock (_loadGate)
        {
            _operationCancellation?.Cancel();
            _operationCancellation?.Dispose();
            var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _operationCancellation = linked;
            var generation = ++_operationGeneration;
            OperationErrorMessage = null;
            OperationState = DesktopLoadState.Loading;
            return (generation, linked);
        }
    }

    private bool IsCurrentOperation(long generation)
    {
        lock (_loadGate) return generation == _operationGeneration && !_isDisposed;
    }

    private void EndOperation(long generation, CancellationTokenSource cancellation)
    {
        lock (_loadGate)
        {
            if (generation == _operationGeneration && ReferenceEquals(_operationCancellation, cancellation))
                _operationCancellation = null;
        }
        cancellation.Dispose();
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;
        CancellationTokenSource? cancellation;
        lock (_loadGate)
        {
            _loadGeneration++;
            _operationGeneration++;
            cancellation = _loadCancellation;
            _loadCancellation = null;
            var operationCancellation = _operationCancellation;
            _operationCancellation = null;
            operationCancellation?.Cancel();
            operationCancellation?.Dispose();
        }
        cancellation?.Cancel();
        cancellation?.Dispose();
        GC.SuppressFinalize(this);
    }
}

/// <summary>Common read metadata shown next to a card, table or event stream.</summary>
public sealed record DesktopSnapshotInfo(
    SnapshotFreshness Freshness,
    DateTimeOffset? ObservedAt,
    string? Source,
    string? SourceVersion,
    string? RequestId,
    DateTimeOffset? ExpiresAt,
    string? ErrorCode = null,
    string? ErrorMessage = null)
{
    public bool IsStale => Freshness is SnapshotFreshness.Stale or SnapshotFreshness.Expired;
    public bool IsFresh => Freshness == SnapshotFreshness.Fresh;

    public static DesktopSnapshotInfo From(SnapshotState snapshot)
        => new(snapshot.Freshness, snapshot.ObservedAt, snapshot.Source, snapshot.SourceVersion,
            snapshot.RequestId, snapshot.ExpiresAt, snapshot.ErrorCode, snapshot.ErrorMessage);
}
