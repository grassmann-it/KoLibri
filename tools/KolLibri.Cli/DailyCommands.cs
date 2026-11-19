using System.Globalization;
using System.Text.Json;
using KolArbitrage.Core.Accounts;
using KolArbitrage.Core.Adventure;
using KolArbitrage.Core.Daily;
using KolArbitrage.Core.Data;
using KolArbitrage.Core.Integration;

internal static partial class KolibriCli
{
    private static async Task<int> DailyCommandAsync(string[] args)
    {
        if (args.Length == 0 || IsHelp(args[0]))
        {
            PrintDailyHelp();
            return 0;
        }

        return args[0].ToLowerInvariant() switch
        {
            "campfire" => DailyCampfireAsync(args[1..]),
            "hermit" => DailyHermitAsync(args[1..]),
            "internet-shop" or "internetshop" => DailyInternetShopAsync(args[1..]),
            "bacon" or "bacons" => DailyBaconAsync(args[1..]),
            "routine" => DailyRoutineAsync(args[1..]),
            "daily-go" or "go" => DailyGoAsync(args[1..]),
            "daily-bridge" or "bridge" => await DailyBridgeAsync(args[1..]),
            "breakfast" => await DailyBreakfastAsync(args[1..]),
            _ => Fail($"Unbekannter daily-Befehl: {args[0]}")
        };
    }

    private static int DailyCampfireAsync(string[] args)
    {
        var snapshot = new CampfireLeavesSnapshot
        {
            CampfireLeaves = ReadInt(args, "--leaves", 0),
            MinimumReserve = ReadInt(args, "--reserve", 3),
            DayShortnerPrice = long.TryParse(GetOption(args, "--shortner-price"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var price) ? Math.Max(0, price) : long.MaxValue,
            AvailableMeat = long.TryParse(GetOption(args, "--meat"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var meat) ? Math.Max(0, meat) : 0,
            IsUsefulDay = !HasFlag(args, "--not-useful"),
            Knucklebones = ReadInt(args, "--knucklebones", 0)
        };
        var planner = new CampfireLeavesPlanner();
        var plan = planner.Plan(snapshot);
        WriteJson(new
        {
            command = "daily campfire",
            readOnly = true,
            previewOnly = true,
            plan.ShouldBurn,
            plan.Verdict,
            plan.ShouldBuyDayShortner,
            plan.Rationale,
            plan.Steps,
            plan.Snapshot,
            note = "Preview only; no choice.php POST is sent. Re-run with --execute --approval-token via the bounded-runner bridge."
        });
        return 0;
    }

    private static int DailyHermitAsync(string[] args)
    {
        var permits = Math.Max(0, ReadInt(args, "--permits", 3));
        var clovers = Math.Max(0, ReadInt(args, "--clovers", 0));
        var wishes = Math.Max(0, ReadInt(args, "--wishes", 0));
        var reserveAdventureValue = long.TryParse(GetOption(args, "--adventure-value"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var adv) ? Math.Max(0, adv) : 0;
        var cloverFloor = long.TryParse(GetOption(args, "--clover-floor"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var floor) ? Math.Max(0, floor) : 0;
        var expectedClover = long.TryParse(GetOption(args, "--clover-sale"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var sale) ? Math.Max(0, sale) : 0;
        var wishBuy = Math.Max(0, ReadInt(args, "--wish-buy", 0));
        var wishPrice = long.TryParse(GetOption(args, "--wish-price"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var price) ? Math.Max(0, price) : 0;

        var planner = new HermitPlanner();
        var plan = planner.Plan(new HermitSnapshot
        {
            Permits = permits,
            Clovers = clovers,
            WishesAvailable = wishes,
            ReserveAdventureValue = reserveAdventureValue,
            MinimumCloverSaleValue = cloverFloor,
            ExpectedCloverSaleValue = expectedClover,
            WishBuyQuantity = wishBuy,
            WishUnitPrice = wishPrice
        });

        WriteJson(new
        {
            command = "daily hermit",
            readOnly = true,
            previewOnly = true,
            plan.Verdict,
            plan.ShouldUseClover,
            plan.ShouldBuyWishes,
            plan.Rationale,
            plan.Steps,
            plan.Snapshot,
            note = "Preview only; no choice.php POST is sent."
        });
        return 0;
    }

    private static int DailyInternetShopAsync(string[] args)
    {
        var pulled = GetOptions(args, "--already-claimed").ToArray();
        var meat = long.TryParse(GetOption(args, "--meat"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var meatOverride) ? Math.Max(0, meatOverride) : 0;
        var plan = new DailyInternetShopPlanner().Plan(new InternetShopSnapshot
        {
            AlreadyPulledItemIds = pulled,
            AvailableMeat = meat
        });
        WriteJson(new
        {
            command = "daily internet-shop",
            readOnly = true,
            previewOnly = true,
            plan.Verdict,
            plan.Rationale,
            plan.Steps,
            plan.Skipped,
            note = "Preview only; no choice POST is sent. The bounded runner can replay each step after --execute --approval-token."
        });
        return 0;
    }

    private static int DailyBaconAsync(string[] args)
    {
        var snapshot = new BaconSnapshot
        {
            OwnedMachines = ReadInt(args, "--owned", 0),
            MallPricePerMachine = long.TryParse(GetOption(args, "--mall-price"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var mall) ? Math.Max(0, mall) : 0,
            PricegunMedianSellPrice = long.TryParse(GetOption(args, "--median"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var median) ? Math.Max(0, median) : 0,
            AvailableMeat = long.TryParse(GetOption(args, "--meat"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var meat) ? Math.Max(0, meat) : 0,
            MinimumProfitPerUnit = long.TryParse(GetOption(args, "--min-profit"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var minProfit) ? Math.Max(0, minProfit) : 50_000,
            MaximumBuyQuantity = ReadInt(args, "--max-qty", 10)
        };
        var plan = new BaconArbitragePlanner().Plan(snapshot);
        WriteJson(new
        {
            command = "daily bacon",
            readOnly = true,
            previewOnly = true,
            plan.ShouldBuy,
            plan.Verdict,
            plan.SuggestedQuantity,
            plan.ExpectedProfitPerUnit,
            plan.Rationale,
            plan.Snapshot,
            note = "Preview only; no mall purchase is sent."
        });
        return 0;
    }

    private static void PrintDailyHelp()
    {
        Console.WriteLine("  daily campfire [--leaves N] [--reserve N] [--knucklebones N]");
        Console.WriteLine("                 [--shortner-price N] [--meat N] [--not-useful]");
        Console.WriteLine("  daily hermit [--permits N] [--clovers N] [--adventure-value N]");
        Console.WriteLine("              [--clover-floor N] [--reserve N]");
        Console.WriteLine("  daily internet-shop [--already-claimed NAME] [--meat N]");
        Console.WriteLine("  daily bacon [--owned N] [--mall-price N] [--median N] [--meat N]");
        Console.WriteLine("               [--min-profit N] [--max-qty N]");
        Console.WriteLine("  daily routine [--leaves N] [--permits N] [--clovers N] [--main-meat N] [--mallbot-meat N]");
        Console.WriteLine("                 [--bag-price N] [--bag-stock N] [--already-claimed NAME]");
        Console.WriteLine("                 [--mall-price N] [--bacon-median N] [--owned-machines N]");
        Console.WriteLine("                 [--adventure-value N] [--clover-floor N] [--clover-sale N]");
        Console.WriteLine("                 [--output PATH] [--execute --approval-token TOKEN --role main|mallbot]");
        Console.WriteLine("  daily-go [--role main|mallbot|all] [--main-leaves N] [--mallbot-leaves N] [--main-meat N]");
        Console.WriteLine("           [--mallbot-meat N] [--main-permits N] [--mallbot-permits N]");
        Console.WriteLine("           [--bag-price N] [--bag-stock N] [--execute --approval-token TOKEN]");
        Console.WriteLine("           [--already-claimed NAME] [--output PATH]");
    }

    private static AdventureExecutionGateway CreateAdventureGateway(AccountRole role, string? approvalToken)
    {
        var token = approvalToken ?? throw new ArgumentException(
            "Live daily execution requires --approval-token.");
        var approval = new DelegateAdventureApprovalHook((_, _) =>
            ValueTask.FromResult(AdventureApprovalDecision.Allow(token)));
        var verification = new DelegateAdventureVerificationHook((context, _) =>
            ValueTask.FromResult(context.Response.Accepted != false
                ? AdventureVerificationResult.Success()
                : AdventureVerificationResult.Failed("The live response was rejected by KoL.")));
        var session = KoLAdventureSessionFactory
            .CreateAsync(role, approval, verification, cancellationToken: CancellationToken.None)
            .GetAwaiter()
            .GetResult();
        return session.Gateway;
    }

    private static int DailyGoAsync(string[] args)
    {
        var roles = ResolveRoles(args);
        if (roles.Count == 0)
            throw new ArgumentException("daily-go braucht mindestens eine Rolle (main, mallbot, all).");

        var defaultSnapshot = BuildDailySnapshot(args);

        var overrides = roles.Select(role =>
        {
            var snapshot = defaultSnapshot;
            var prefix = role == AccountRole.Main ? "main" : "mallbot";
            var leavesOverride = ReadInt(args, $"--{prefix}-leaves", -1);
            var meatOverride = long.TryParse(GetOption(args, $"--{prefix}-meat"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var m) ? m : -1L;
            var permitsOverride = ReadInt(args, $"--{prefix}-permits", -1);
            var bagPriceOverride = long.TryParse(GetOption(args, $"--{prefix}-bag-price"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var bp) ? bp : -1L;
            if (leavesOverride >= 0) snapshot = snapshot with { CampfireLeaves = leavesOverride };
            if (meatOverride >= 0) snapshot = role == AccountRole.Main
                ? snapshot with { MainMeat = meatOverride }
                : snapshot with { MallbotMeat = meatOverride };
            if (permitsOverride >= 0) snapshot = snapshot with { HermitPermits = permitsOverride };
            if (bagPriceOverride >= 0) snapshot = snapshot with { ForeignBribeBagPrice = bagPriceOverride };
            return new RoleOverride(role, snapshot);
        }).ToArray();

        var token = GetOption(args, "--approval-token");
        var useBridge = HasFlag(args, "--use-bridge");
        BridgeAuditLog? bridgeAudit = useBridge ? new BridgeAuditLog(BridgeAuditLog.DefaultPath()) : null;
        var orchestrator = useBridge
            ? new DailyOrchestrator(
                gatewayFactory: role => CreateAdventureGateway(role, token),
                approvalToken: token,
                stateReaderFactory: _ => new OfflineAccountDailyStateReader(),
                freePullFactory: role => CreateFreePullDispatcher(role),
                credentialsFactory: role => TryGetCredentials(role))
            : new DailyOrchestrator(
                gatewayFactory: role => CreateAdventureGateway(role, token),
                approvalToken: token);
        var plan = orchestrator.BuildPlan(overrides, defaultSnapshot);

        DailyOrchestrationResult? execution = null;
        if (HasFlag(args, "--execute"))
        {
            execution = useBridge
                ? orchestrator.ExecuteBridgeAsync(plan, audit: bridgeAudit).GetAwaiter().GetResult()
                : orchestrator.ExecuteAsync(plan, failFast: HasFlag(args, "--fail-fast"))
                    .GetAwaiter().GetResult();
        }
        bridgeAudit?.Dispose();

        WriteJson(new
        {
            command = "daily-go",
            readOnly = execution is null,
            previewOnly = execution is null,
            executed = execution is not null,
            plan.GeneratedAt,
            plan.TotalApprovableSteps,
            roles = plan.Plans.Select(p => new
            {
                role = p.Role.ToString(),
                approvableSteps = p.Preview.ApprovableSteps,
                verdict = p.Preview.Verdict,
                advisories = p.Preview.Advisories
            }).ToArray(),
            execution = execution is null ? null : new
            {
                succeededRoleCount = execution.SucceededRoleCount,
                failedRoleCount = execution.FailedRoleCount,
                perRole = execution.Results.Select(r => new
                {
                    role = r.Plan.Role.ToString(),
                    executed = r.Execution.Executed,
                    steps = r.Execution.Steps.Select(s => new
                    {
                        s.StepKind, s.Location, status = s.Status.ToString(),
                        s.StopReason, s.RequestId, s.Message
                    }).ToArray()
                }).ToArray()
            }
        });

        var outputPath = GetOption(args, "--output");
        if (!string.IsNullOrWhiteSpace(outputPath))
        {
            var full = Path.GetFullPath(outputPath);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, JsonSerializer.Serialize(new
            {
                generatedAt = plan.GeneratedAt,
                totalApprovableSteps = plan.TotalApprovableSteps,
                roles = plan.Plans.Select(p => new
                {
                    role = p.Role.ToString(),
                    verdict = p.Preview.Verdict,
                    approvableSteps = p.Preview.ApprovableSteps
                }).ToArray(),
                execution
            }, JsonOptions));
            Console.Error.WriteLine($"daily-go report written to {full}");
        }
        return 0;
    }

    private static List<AccountRole> ResolveRoles(string[] args)
    {
        var requested = (GetOption(args, "--role") ?? "main").Trim().ToLowerInvariant();
        if (requested is "all" or "both")
            return new() { AccountRole.Main, AccountRole.Mallbot };
        return new() { ParseRole(requested) };
    }

    private static int DailyRoutineAsync(string[] args)
    {
        var snapshot = new DailyRoutineSnapshot
        {
            CampfireLeaves = ReadInt(args, "--leaves", 0),
            CampfireReserve = ReadInt(args, "--campfire-reserve", 3),
            DayShortnerPrice = long.TryParse(GetOption(args, "--shortner-price"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var sp) ? Math.Max(0, sp) : long.MaxValue,
            MainMeat = long.TryParse(GetOption(args, "--main-meat"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var mm) ? Math.Max(0, mm) : 0,
            HermitPermits = ReadInt(args, "--permits", 0),
            Clovers = ReadInt(args, "--clovers", 0),
            AdventureReserveValue = long.TryParse(GetOption(args, "--adventure-value"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var adv) ? Math.Max(0, adv) : 0,
            CloverFloorValue = long.TryParse(GetOption(args, "--clover-floor"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var cf) ? Math.Max(0, cf) : 0,
            ExpectedCloverSaleValue = long.TryParse(GetOption(args, "--clover-sale"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var cs) ? Math.Max(0, cs) : 0,
            WishBuyQuantity = ReadInt(args, "--wish-buy", 0),
            WishUnitPrice = long.TryParse(GetOption(args, "--wish-price"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var wp) ? Math.Max(0, wp) : 0,
            UselessItems = ReadInt(args, "--useless-items", 0),
            AlreadyPulledItemIds = GetOptions(args, "--already-claimed").ToArray(),
            OwnedMachines = ReadInt(args, "--owned-machines", 0),
            MallPricePerMachine = long.TryParse(GetOption(args, "--mall-price"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var mp) ? Math.Max(0, mp) : 0,
            PricegunMedianBaconPrice = long.TryParse(GetOption(args, "--bacon-median"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var bm) ? Math.Max(0, bm) : 0,
            MallbotMeat = long.TryParse(GetOption(args, "--mallbot-meat"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var mb) ? Math.Max(0, mb) : 0,
            ForeignBribeBagPrice = long.TryParse(GetOption(args, "--bag-price"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var bp) ? Math.Max(0, bp) : long.MaxValue,
            ForeignBribeBagStock = ReadInt(args, "--bag-stock", 0),
            PilotBagQuantity = ReadInt(args, "--pilot-bags", 40),
            MallbotMeatReserve = long.TryParse(GetOption(args, "--mallbot-reserve"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var mr) ? Math.Max(0, mr) : 1_000_000
        };

        var report = new DailyRoutineRunner().Run(snapshot);

        // Live execution path: --execute plus --approval-token drives the
        // bounded runner. Without the token we stay in preview mode.
        DailyRoutineExecutionResult? executionResult = null;
        if (HasFlag(args, "--execute"))
        {
            var token = GetOption(args, "--approval-token");
            var executor = new DailyRoutineExecutor(
                gatewayFactory: role => CreateAdventureGateway(role, token),
                approvalToken: token);
            executionResult = executor.ExecuteAsync(
                ParseRole(GetOption(args, "--role") ?? "main"),
                report).AsTask().GetAwaiter().GetResult();
        }

        WriteJson(new
        {
            command = "daily routine",
            readOnly = executionResult is null,
            previewOnly = executionResult is null,
            executed = executionResult?.Executed ?? false,
            report.GeneratedAt,
            report.Verdict,
            report.Counts,
            report.ApprovableSteps,
            report.Advisories,
            subPlans = new
            {
                campfire = new
                {
                    report.Campfire.Verdict,
                    report.Campfire.ShouldBurn,
                    report.Campfire.ShouldBuyDayShortner,
                    report.Campfire.Rationale,
                    report.Campfire.Steps
                },
                hermit = new
                {
                    report.Hermit.Verdict,
                    report.Hermit.ShouldUseClover,
                    report.Hermit.ShouldBuyWishes,
                    report.Hermit.Rationale,
                    report.Hermit.Steps
                },
                internetShop = new
                {
                    report.InternetShop.Verdict,
                    report.InternetShop.Rationale,
                    report.InternetShop.Steps,
                    report.InternetShop.Skipped
                },
                bacon = new
                {
                    report.Bacon.ShouldBuy,
                    report.Bacon.Verdict,
                    report.Bacon.SuggestedQuantity,
                    report.Bacon.ExpectedProfitPerUnit,
                    report.Bacon.Rationale
                },
                foreignBribes = report.ForeignBribes
            },
            execution = executionResult is null
                ? null
                : new
                {
                    role = executionResult.Role.ToString(),
                    auditPath = executionResult.AuditPath,
                    steps = executionResult.Steps.Select(s => new
                    {
                        s.StepKind, s.Location, s.RunId, status = s.Status.ToString(),
                        s.RequestId, s.StopReason, s.Message
                    }).ToArray()
                },
            note = executionResult is null
                ? "Preview only; nothing was sent. Use the bounded-runner bridge to commit each step after --execute --approval-token."
                : $"Live execution {executionResult.Steps.Count} step(s); see auditPath + steps for details."
        });

        var outputPath = GetOption(args, "--output");
        if (!string.IsNullOrWhiteSpace(outputPath))
        {
            var full = Path.GetFullPath(outputPath);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, JsonSerializer.Serialize(new
            {
                generatedAt = report.GeneratedAt,
                verdict = report.Verdict,
                counts = report.Counts,
                approvableSteps = report.ApprovableSteps,
                advisories = report.Advisories,
                snapshot,
                execution = executionResult is null ? null : new
                {
                    executionResult.Role,
                    executionResult.AuditPath,
                    executionResult.Steps
                }
            }, JsonOptions));
            Console.Error.WriteLine($"daily routine report written to {full}");
        }
        return 0;
    }

    private static async Task<int> DailyBreakfastAsync(string[] args)
    {
        // "Breakfast" reads the live accounts, derives the daily numbers
        // itself and runs the routine. The operator passes no inventory
        // flags — everything comes from KoL.
        var roles = ResolveRoles(args);
        if (roles.Count == 0)
            throw new ArgumentException(
                "breakfast braucht mindestens eine Rolle (main, mallbot, all).");
        var token = GetOption(args, "--approval-token");
        var execute = HasFlag(args, "--execute");
        var baseline = BuildDailySnapshot(args);

        var perRole = new List<object>();
        var bridge = new AutoCombatDailyBridge();
        BridgeAuditLog? audit = execute && !string.IsNullOrWhiteSpace(token)
            ? new BridgeAuditLog(BridgeAuditLog.DefaultPath())
            : null;
        try
        {
            foreach (var role in roles)
            {
                var client = await ReadClientAsync(role);
                var builder = new LiveDailySnapshotBuilder(client.Api);
                var live = await builder.BuildAsync(baseline);
                var plan = bridge.Plan(live.State, live.Snapshot);
                AutoCombatDailyBridge.BridgeExecutionResult? execution = null;
                if (execute && !string.IsNullOrWhiteSpace(token))
                {
                    var gateway = CreateAdventureGateway(role, token);
                    execution = await bridge.ExecuteAsync(
                        plan.Steps, gateway,
                        freePullDispatcher: CreateFreePullDispatcher(role),
                        freePullCredentials: TryGetCredentials(role),
                        audit: audit, accountId: role.ToString());
                }
                perRole.Add(new
                {
                    role = role.ToString(),
                    live = new
                    {
                        live.Meat,
                        live.Adventures,
                        live.Level,
                        live.ClassId,
                        live.Leaves,
                        live.Clovers,
                        live.Permits,
                        live.WorthlessItems,
                        live.Bacon,
                        live.BaconMachines
                    },
                    steps = plan.Steps.Select(s => new
                    {
                        kind = s.GetType().Name,
                        choiceId = s is ChoiceStepSpec cs ? cs.ChoiceId : null,
                        optionId = s is ChoiceStepSpec cs2 ? cs2.OptionId : null,
                        itemId = s is ItemStepSpec is2 ? is2.ItemId : null,
                        quantity = s is ItemStepSpec is3 ? is3.Quantity : (int?)null
                    }),
                    allowlist = plan.AllowlistedChoiceIds,
                    executed = execution?.Executed ?? false,
                    auditPath = execution?.AuditPath
                });
            }
        }
        finally
        {
            audit?.Dispose();
        }

        WriteJson(new
        {
            command = "daily breakfast",
            readOnly = !execute,
            executed = execute && !string.IsNullOrWhiteSpace(token),
            roles = perRole
        });
        return 0;
    }

    private static async Task<int> DailyBridgeAsync(string[] args)
    {
        // The bridge path runs AutoCombatDailyBridge directly with a
        // snapshot-derived AccountDailyState. Read-only is fully self-
        // contained; with --execute + --approval-token we route each
        // choice step through the bounded runner and write a hash-chained
        // audit row per submission.
        var roles = ResolveRoles(args);
        var token = GetOption(args, "--approval-token");
        var execute = HasFlag(args, "--execute");
        var snapshot = BuildDailySnapshot(args);
        var bridge = new AutoCombatDailyBridge();
        var emptyState = new AccountDailyState
        {
            Role = AccountRole.Main,
            Status = new KoLStatusSnapshot("offline", 0, 0, 0, DateTimeOffset.UtcNow)
                with { ClassId = "" },
            Inventory = new KoLInventorySnapshot(Array.Empty<KoLInventoryItem>(), DateTimeOffset.UtcNow),
            ObservedAt = DateTimeOffset.UtcNow
        };
        var plan = bridge.Plan(emptyState, snapshot);
        AutoCombatDailyBridge.BridgeExecutionResult? bridgeExecution = null;
        if (execute && !string.IsNullOrWhiteSpace(token))
        {
            using var audit = new BridgeAuditLog(BridgeAuditLog.DefaultPath());
            // One gateway per role; the bridge posts each step in turn.
            var tasks = roles.Select(async role =>
            {
                var gateway = CreateAdventureGateway(role, token);
                var exec = await bridge.ExecuteAsync(
                    plan.Steps, gateway, audit: audit,
                    accountId: role.ToString()).ConfigureAwait(false);
                return (role, exec);
            }).ToArray();
            var results = await Task.WhenAll(tasks).ConfigureAwait(false);
            bridgeExecution = results[0].exec;
        }
        var result = new
        {
            command = "daily-bridge",
            readOnly = !execute,
            executed = bridgeExecution?.Executed ?? (execute && !string.IsNullOrWhiteSpace(token)),
            roles = roles.Select(r => r.ToString()).ToArray(),
            steps = plan.Steps.Select(s => new
            {
                kind = s.GetType().Name,
                @switch = s
            }),
            allowlist = plan.AllowlistedChoiceIds,
            auditPath = bridgeExecution?.AuditPath,
            executedCount = bridgeExecution is null ? 0 : plan.Steps.OfType<ChoiceStepSpec>().Count()
        };
        WriteJson(result);
        return 0;
    }

    /// <summary>Snapshot-derived <see cref="IAccountDailyStateReader"/> that
    /// returns an empty state, so the bridge predicate layer fires only on
    /// snapshot-driven decisions (campfire leaves, internet-shop pulls).
    /// Item-driven decisions (archaeologist + spade) stay quiet.</summary>
    private sealed class OfflineAccountDailyStateReader : IAccountDailyStateReader
    {
        public Task<AccountDailyState> ReadAsync(AccountRole role, CancellationToken cancellationToken)
        {
            return Task.FromResult(new AccountDailyState
            {
                Role = role,
                Status = new KoLStatusSnapshot("offline", 0, 0, 0, DateTimeOffset.UtcNow) with { ClassId = "" },
                Inventory = new KoLInventorySnapshot(Array.Empty<KoLInventoryItem>(), DateTimeOffset.UtcNow),
                ObservedAt = DateTimeOffset.UtcNow
            });
        }
    }

    private static FreePullDispatcher? CreateFreePullDispatcher(AccountRole role)
    {
        // The free-pull dispatcher needs a logged-in HttpClient. We reuse
        // the same authenticated client as the adventure gateway so the
        // cookie travels with both request paths.
        var http = ReadAuthenticatedHttpAsync(role).GetAwaiter().GetResult();
        return new FreePullDispatcher(http, FreePullDispatcher.DefaultBaseUri);
    }

    private static AccountCredentials? TryGetCredentials(AccountRole role)
    {
        try
        {
            var manager = AccountSessionManager.CreateDefault();
            manager.InitializeAsync().GetAwaiter().GetResult();
            return manager.GetSessionAsync(role).GetAwaiter().GetResult()?.Credentials;
        }
        catch
        {
            return null;
        }
    }

    private static DailyRoutineSnapshot BuildDailySnapshot(string[] args)
    {
        return new DailyRoutineSnapshot
        {
            CampfireLeaves = ReadInt(args, "--leaves", 0),
            CampfireReserve = ReadInt(args, "--campfire-reserve", 3),
            DayShortnerPrice = long.TryParse(GetOption(args, "--shortner-price"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var sp) ? Math.Max(0, sp) : long.MaxValue,
            MainMeat = long.TryParse(GetOption(args, "--main-meat"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var mm) ? Math.Max(0, mm) : 0,
            HermitPermits = ReadInt(args, "--permits", 0),
            Clovers = ReadInt(args, "--clovers", 0),
            AdventureReserveValue = long.TryParse(GetOption(args, "--adventure-value"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var adv) ? Math.Max(0, adv) : 0,
            CloverFloorValue = long.TryParse(GetOption(args, "--clover-floor"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var cf) ? Math.Max(0, cf) : 0,
            ExpectedCloverSaleValue = long.TryParse(GetOption(args, "--clover-sale"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var cs) ? Math.Max(0, cs) : 0,
            WishBuyQuantity = ReadInt(args, "--wish-buy", 0),
            WishUnitPrice = long.TryParse(GetOption(args, "--wish-price"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var wp) ? Math.Max(0, wp) : 0,
            UselessItems = ReadInt(args, "--useless-items", 0),
            AlreadyPulledItemIds = GetOptions(args, "--already-claimed").ToArray(),
            OwnedMachines = ReadInt(args, "--owned-machines", 0),
            MallPricePerMachine = long.TryParse(GetOption(args, "--mall-price"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var mp) ? Math.Max(0, mp) : 0,
            PricegunMedianBaconPrice = long.TryParse(GetOption(args, "--bacon-median"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var bm) ? Math.Max(0, bm) : 0,
            MallbotMeat = long.TryParse(GetOption(args, "--mallbot-meat"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var mb) ? Math.Max(0, mb) : 0,
            ForeignBribeBagPrice = long.TryParse(GetOption(args, "--bag-price"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var bp) ? Math.Max(0, bp) : long.MaxValue,
            ForeignBribeBagStock = ReadInt(args, "--bag-stock", 0),
            PilotBagQuantity = ReadInt(args, "--pilot-bags", 40),
            MallbotMeatReserve = long.TryParse(GetOption(args, "--mallbot-reserve"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var mr) ? Math.Max(0, mr) : 1_000_000
        };
    }
}
