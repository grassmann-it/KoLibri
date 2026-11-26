# KoLibri 0.5 – Bestandsaufnahme der Core-Nutzung (generiert)

Stand: `release/0.4.0` @ 911517b. Erzeugt per Skript aus allen `public`-Typdeklarationen in `src/KoLibri.Core` und `src/LibKolibri/src/LibKolibri` und einem Wort-Abgleich der Typnamen in `tools/KolLibri.Cli` (C), `tools/KolLibri.Mcp` (M) und `KoLibri.Desktop` (D). Heuristik: ein Treffer heißt, der Name kommt im App-Code vor; gleichnamige App-eigene Typen können falsche Treffer erzeugen. `Nur Tests` = von keiner App, aber von `tests/`, `OfflineDemo`, `UiQa` oder `ConsumerSmoke` benutzt. `Lib-intern` = Anzahl anderer Lib-Dateien, die den Namen nennen.

**Summe:** 1195 öffentliche Typen (inkl. verschachtelter), davon 415 von mindestens einer App genutzt (CLI 195, MCP 154, Desktop 269).

| Fähigkeit | Typen | von Apps genutzt | nur Tests/Demos | nirgends außerhalb der Lib |
|---|---:|---:|---:|---:|
| M0M1-/Gateway-Schicht (löschen) | 164 | 49 | 26 | 89 |
| Safety: Freigaben, Budgets, Ledger, Audit (optionales Paket) | 146 | 28 | 29 | 89 |
| Session/Login | 45 | 19 | 18 | 8 |
| Roher Seitenabruf über Limiter | 31 | 2 | 18 | 11 |
| Status/Inventar | 71 | 20 | 13 | 38 |
| Konsum/Rest/Cast/Equip | 50 | 38 | 1 | 11 |
| Daily | 90 | 38 | 7 | 45 |
| Abenteuer/Kampf/Choice | 291 | 118 | 53 | 120 |
| Mall-Suche/-Kauf | 76 | 37 | 15 | 24 |
| Eigener Shop | 19 | 6 | 4 | 9 |
| Pirate-Keys | 6 | 3 | 1 | 2 |
| Kmail | 6 | 6 | 0 | 0 |
| Relay | 3 | 2 | 1 | 0 |
| Extras außerhalb der Lib (Arbitrage, CRM, Analytics, Clan, Chat, Coinmaster …) | 197 | 49 | 42 | 106 |


## M0M1-/Gateway-Schicht (löschen)

### Von Apps genutzt

- `Lib/CoreGatewayAdapters.cs`: `CoreAccountGateway`[M], `CoreMallGateway`[M]
- `Lib/LibKolibriContracts.cs`: `IAccountGateway`[MD], `IMallGateway`[MD], `IAdventureGateway`[MD], `ITransferGateway`[M], `TransferRequest`[M]
- `Lib/LibKolibriRuntime.cs`: `LibKolibriRuntime`[M]
- `Lib/M0M1Contracts.cs`: `GatewayState`[D], `PreviewState`[D], `SnapshotFreshness`[D], `ArbitrageDecision`[D], `ApprovalState`[D], `ShopMutationState`[D], `AuditEventKind`[D], `SnapshotState`[D], `GatewayDiagnostic`[D], `ExpectedStateCheck`[D], `ExpectedState`[D], `ArbitrageQuery`[D], `ArbitrageCandidate`[D], `ArbitrageQueryResult`[D], `ArbitrageAction`[D], `ArbitragePreviewRequest`[D], `ArbitragePreview`[D], `ApprovalRequest`[D], `ApprovalPreview`[D], `ApprovalDecisionRequest`[D], `ApprovalExpirationRequest`[D], `ApprovalResult`[D], `ApprovalCommitRequest`[D], `ApprovalVerificationRequest`[D], `ApprovalVerification`[D], `IApprovalGateway`[D], `ShopPriceChange`[D], `ShopMutationRequest`[D], `ShopMutationItemResult`[D], `ShopMutationPreview`[D], `IShopMutationGateway`[D], `AuditEvent`[CMD], `AuditLogQuery`[D], `AuditLogResult`[D], `IAuditLogGateway`[D], `IArbitrageGateway`[D]
- `Lib/McpRuntimeBootstrap.cs`: `McpRuntimeBootstrap`[M]
- `Lib/PreviewOnlyTransferGateway.cs`: `PreviewOnlyTransferGateway`[M]
- `Lib/RoleScopedRuntime.cs`: `RoleScopedRuntime`[M]
- `Markt/Mall/CoreArbitrageQueryService.cs`: `ArbitrageQuery`[D], `SnapshotFreshness`[D]

### Nicht von Apps genutzt

- `Lib/CoreGatewayAdapters.cs`: `CoreAdventureGateway` (Lib-intern), `CoreCombatGateway` (Tests), `CoreCoinmasterGateway` (Lib-intern)
- `Lib/CoreGatewayRegistration.cs`: `CoreGatewayRegistration` (tot)
- `Lib/CoreM0M1GatewayAdapters.cs`: `CorePricingGateway` (Lib-intern), `CoreArbitrageGateway` (Lib-intern), `CoreApprovalGateway` (Lib-intern), `CoreAuditLogGateway` (Lib-intern), `CoreSnapshotGateway` (Lib-intern), `CoreItemCatalogGateway` (Lib-intern), `CoreShopMutationGateway` (Lib-intern), `PreviewOnlyShopMutationGateway` (Lib-intern), `UnconfiguredPricingGateway` (Lib-intern), `UnconfiguredArbitrageGateway` (Lib-intern), `UnconfiguredShopMutationGateway` (Lib-intern), `InMemoryAuditLogGateway` (Lib-intern), `InMemorySnapshotGateway` (Lib-intern)
- `Lib/CoreM0M1Registration.cs`: `CoreM0M1RegistrationOptions` (Lib-intern), `CoreM0M1Registration` (tot)
- `Lib/LibKolibriContracts.cs`: `KolibriCapability` (Tests), `ICombatGateway` (Tests), `ICoinmasterGateway` (Lib-intern), `TransferPreview` (Lib-intern), `IKolibriModule` (Lib-intern), `KolibriRuntimeDescriptor` (Lib-intern)
- `Lib/M0M1Contracts.cs`: `QueryState` (Lib-intern), `ItemTradeabilityState` (Lib-intern), `ItemVariantState` (Lib-intern), `ArbitrageQueryRequest` (Lib-intern), `ApprovalPreviewRequest` (tot), `ItemCatalogQuery` (Lib-intern), `ItemCatalogRequest` (tot), `ItemVariantLink` (Lib-intern), `ItemCatalogEntry` (Lib-intern), `ItemCatalogResult` (Lib-intern), `IItemCatalogGateway` (Lib-intern), `ShopMutationChange` (tot), `ShopMutationCommitRequest` (Lib-intern), `ShopMutationResult` (Lib-intern), `AuditAppendResult` (Lib-intern)
- `Lib/M0M1GatewayContracts.cs`: `PricingQuery` (Lib-intern), `PricingRequest` (tot), `PricingResult` (Lib-intern), `IPricingGateway` (Lib-intern), `SnapshotQuery` (Lib-intern), `SnapshotRequest` (tot), `ISnapshotGateway` (Lib-intern)
- `Lib/MainMallbotTransferContracts.cs`: `IMainMallbotTransferGateway` (Lib-intern), `ITransferWorkflowGateway` (Lib-intern), `PreviewOnlyMainMallbotTransferGateway` (Lib-intern), `PreviewOnlyTransferWorkflowGateway` (tot)
- `Lib/MainMallbotTransferGatewayAdapter.cs`: `CoreMainMallbotTransferGateway` (Lib-intern), `KoLMainMallbotTransferGateway` (tot)
- `Lib/RoleScopedRuntime.cs`: `KolibriRuntimeFacade` (tot), `RoleScopedRuntimeExtensions` (tot)
- `Markt/Mall/CoreArbitrageQueryService.cs`: `ArbitrageObservation` (Tests), `ArbitrageQueryOptions` (Tests), `ArbitrageQueryRequest` (Lib-intern), `CoreSnapshotState` (Lib-intern), `CoreArbitrageDecision` (Tests), `CoreArbitrageCandidate` (tot), `CoreArbitrageQueryResult` (Lib-intern), `CoreQueryState` (Tests), `CoreQueryDiagnostic` (Lib-intern), `CoreArbitrageQueryService` (Tests), `ArbitrageQueryService` (Lib-intern)
- `Markt/Mall/CoreShopQueryService.cs`: `ShopObservation` (Tests), `ShopQueryOptions` (Tests), `ShopQuery` (Tests), `ShopQueryRequest` (tot), `CoreShopDecision` (tot), `CoreShopCandidate` (tot), `CoreShopQueryResult` (tot), `CoreShopQueryService` (Tests), `ShopQueryService` (tot)
- `Markt/Mall/MainMallbotTransferGateway.cs`: `IMainMallbotTransferTransport` (Lib-intern), `MainMallbotTransferAccountBinding` (Tests), `MainMallbotTransferRefresh` (Tests), `MainMallbotTransferTransportCommit` (Tests), `MainMallbotTransferReceipt` (tot), `MainMallbotTransferMode` (tot), `MainMallbotTransferPolicy` (Tests), `MainMallbotTransferGateway` (Tests), `FixtureMainMallbotTransferTransport` (Tests), `MainMallbotTransferFixtureTransport` (tot), `KoLMainMallbotTransferTransport` (Tests), `KoLMainMallbotTransferHttpOptions` (tot), `KoLMainMallbotTransferHttpAdapter` (tot)
- `Markt/Mall/ReadQueryContracts.cs`: `ReadSnapshotKind` (Lib-intern), `ReadFreshness` (Tests), `StaleReadMode` (tot), `ReadQueryPolicy` (Tests), `ReadQueryOptions` (Tests), `ArbitrageObservationRequest` (Tests), `ReadQueryRequest` (tot), `IPricegunReadClient` (Tests), `IReadCacheInvalidator` (Lib-intern), `ReadRequestIds` (Lib-intern), `PricegunReadClientAdapter` (tot), `ReadClientAdapterExtensions` (tot), `ReadAccountSource` (Tests), `AccountReadSource` (tot), `AccountReadRegistration` (tot), `ReadSnapshot` (Lib-intern), `ReadQueryDiagnostic` (Lib-intern), `AccountReadObservation` (Lib-intern), `ArbitrageItemObservation` (Lib-intern), `ArbitrageBatchObservation` (Lib-intern), `ReadObservationBatch` (tot), `IReadQueryCoordinator` (Lib-intern), `IReadSnapshotCoordinator` (Lib-intern), `IReadCoordinator` (Lib-intern)
- `Markt/Mall/ReadQueryCoordinator.cs`: `ReadQueryCoordinator` (Tests), `SharedReadQueryCoordinator` (tot), `SnapshotCoordinator` (tot)

## Safety: Freigaben, Budgets, Ledger, Audit (optionales Paket)

### Von Apps genutzt

- `Basis/Audit/AuditEventModels.cs`: `AuditEventKind`[D], `AuditEvent`[CMD]
- `Basis/Audit/AuditHashChain.cs`: `SealedAuditEvent`[C]
- `Basis/Audit/JsonlAuditEventJournal.cs`: `IAuditEventJournal`[MD], `AuditEventJournal`[M]
- `Basis/Security/ApprovalTokenStore.cs`: `ApprovalToken`[CD], `ApprovalTokenStore`[CD]
- `Lib/Access/AccessGate.cs`: `PlanTicket`[M], `AccessGate`[M]
- `Lib/Access/Grant.cs`: `RightLimit`[M], `Grant`[MD], `AccessDecision`[M], `AccessRequest`[M]
- `Lib/Access/GrantStore.cs`: `GrantStore`[MD]
- `Lib/Access/Operations.cs`: `OperationKind`[M], `Rights`[MD], `OperationCatalog`[M]
- `Lib/Access/UsageLedger.cs`: `UsageReservation`[M], `UsageLedger`[MD]
- `Markt/Arbitrage/ArbitrageSpendLedger.cs`: `ArbitrageSpendLedger`[C]
- `Markt/Arbitrage/FileSpendLedger.cs`: `FileSpendLedger`[D], `SharedMallSpendLedger`[C]
- `Markt/Mall/PurchaseApprovalModels.cs`: `PurchaseRequest`[MD], `PurchaseBudget`[M], `PurchaseApprovalPolicy`[M], `PurchaseApprovalService`[M]
- `Markt/Mall/TransferApprovalModels.cs`: `TransferRequest`[M]
- `Spiel/Daily/BridgeAuditLog.cs`: `BridgeAuditLog`[CMD]

### Nicht von Apps genutzt

- `Basis/Audit/AuditEventJournalExtensions.cs`: `AuditEventJournalExtensions` (tot)
- `Basis/Audit/AuditEventModels.cs`: `AuditEventPhase` (Tests), `AuditLifecyclePhase` (tot), `AuditEventType` (tot), `AuditIds` (Lib-intern), `AuditEventQuery` (Tests)
- `Basis/Audit/AuditHashChain.cs`: `AuditHashChain` (Tests), `AuditChainBreak` (Lib-intern)
- `Basis/Audit/DurableSnapshotStore.cs`: `SnapshotKey` (Tests), `SnapshotCachePolicy` (Tests), `SnapshotPolicy` (Lib-intern), `SnapshotStorePolicy` (tot), `SnapshotStoreOptions` (tot), `SnapshotEnvelope` (Tests), `SnapshotReadResult` (tot), `SnapshotStoreException` (Tests), `IDurableSnapshotStore` (Lib-intern), `ILocalSnapshotStore` (tot), `ISnapshotStore` (tot), `DurableSnapshotStore` (Tests), `LocalSnapshotStore` (tot), `JsonFileSnapshotStore` (tot), `JsonSnapshotStore` (tot), `FileSnapshotStore` (tot)
- `Basis/Audit/JsonlAuditEventJournal.cs`: `AuditJournalOptions` (Tests), `AuditJournalFormatException` (Tests), `IAuditLog` (tot), `JsonlAuditEventJournal` (Tests), `JsonlAuditJournal` (tot), `JsonAuditEventJournal` (tot)
- `Basis/Audit/OperationLedgerModels.cs`: `OperationLedgerState` (Tests), `OperationCommitState` (Tests), `OperationApprovalState` (Lib-intern), `OperationAuditEventType` (Lib-intern), `OperationLedgerOptions` (Tests), `OperationPreviewRequest` (Tests), `OperationLedgerPreview` (Lib-intern), `OperationPreviewRegistrationResult` (Lib-intern), `OperationApprovalRequest` (Lib-intern), `OperationApprovalResult` (Lib-intern), `OperationCommitRequest` (Lib-intern), `OperationCommitResult` (Lib-intern), `OperationAuditEvent` (Lib-intern), `OperationLedgerEntry` (Lib-intern), `OperationLedgerSnapshot` (Lib-intern), `OperationLedgerConflictException` (Lib-intern), `OperationLedgerStoreException` (Lib-intern), `OperationLedgerFingerprint` (tot)
- `Basis/Audit/OperationLedgerService.cs`: `IOperationApprovalLedger` (Lib-intern), `IOperationLedger` (Lib-intern), `OperationLedgerService` (Tests), `ApprovalLedgerService` (tot), `OperationApprovalLedger` (tot)
- `Basis/Audit/OperationLedgerStore.cs`: `IOperationLedgerStore` (Lib-intern), `IApprovalLedgerStore` (tot), `InMemoryOperationLedgerStore` (Lib-intern), `JsonOperationLedgerStore` (Tests), `DurableOperationLedgerStore` (tot), `JsonFileOperationLedgerStore` (tot)
- `Lib/Access/Grant.cs`: `AccessCode` (Tests)
- `Lib/Access/Operations.cs`: `OperationInfo` (tot)
- `Lib/Access/UsageLedger.cs`: `UsageEntry` (tot)
- `Markt/Arbitrage/FileSpendLedger.cs`: `SpendLedgerException` (Tests)
- `Markt/Mall/PurchaseApprovalModels.cs`: `PurchaseExpectedState` (tot), `PurchaseObservation` (tot), `PurchaseAbortReason` (tot), `PurchaseVerificationStatus` (tot), `PurchasePreview` (tot), `PurchaseCommitRequest` (tot), `PurchaseCommitResult` (tot), `PurchaseCommitVerifier` (tot)
- `Markt/Mall/ShopRepriceApprovalModels.cs`: `ShopRepriceAbortReason` (Tests), `ShopRepriceOperationStatus` (Lib-intern), `ShopRepriceVerificationStatus` (Lib-intern), `ShopRepriceRequest` (Tests), `ShopRepricePolicy` (Tests), `ShopRepricePreview` (Lib-intern), `ShopRepriceApproval` (Lib-intern), `ShopRepriceApprovalRequest` (Lib-intern), `ShopRepriceRefreshResult` (Lib-intern), `ShopRepriceCommitRequest` (Lib-intern), `ShopRepriceCommitResult` (Lib-intern), `ShopRepriceVerificationResult` (Lib-intern), `ShopRepriceOperationRecord` (tot), `IShopRepriceIdempotencyStore` (Lib-intern), `InMemoryShopRepriceIdempotencyStore` (Lib-intern)
- `Markt/Mall/ShopRepriceApprovalService.cs`: `ShopRepriceApprovalService` (Tests), `ShopRepriceService` (tot), `KoLShopRepriceService` (tot), `ShopRepriceGateway` (tot)
- `Markt/Mall/TransferApprovalModels.cs`: `TransferAbortReason` (Tests), `TransferReservationStatus` (Lib-intern), `TransferVerificationStatus` (Lib-intern), `TransferOperationStatus` (Lib-intern), `TransferReconciliationAction` (tot), `TransferLine` (tot), `TransferObservation` (Tests), `TransferExpectedState` (tot), `TransferReservation` (tot), `TransferApprovalPolicy` (Tests), `TransferPreview` (Lib-intern), `TransferApproval` (Lib-intern), `TransferApprovalRequest` (Lib-intern), `TransferItemCommit` (Lib-intern), `TransferCommitRequest` (Tests), `TransferLineVerification` (tot), `TransferReconciliation` (tot), `TransferCommitResult` (Lib-intern), `TransferVerificationRequest` (Lib-intern), `TransferVerificationResult` (Lib-intern), `TransferOperationRecord` (tot), `ITransferIdempotencyStore` (Lib-intern), `InMemoryTransferIdempotencyStore` (Tests), `ITransferReservationStore` (Lib-intern), `InMemoryTransferReservationStore` (tot), `TransferApprovalService` (Tests), `TransferCommitVerifier` (tot)
- `Spiel/Adventure/AuditedAdventureHooks.cs`: `AuditedAdventureHooks` (Lib-intern)

## Session/Login

### Von Apps genutzt

- `Basis/Accounts/AccountIdentityVerifier.cs`: `AccountIdentityVerifier`[C]
- `Basis/Accounts/AccountModels.cs`: `AccountRole`[CMD], `AccountProfile`[CD], `AccountCredentials`[CM], `AccountSession`[CMD], `AccountConfigurationException`[M], `AccountIdentityMismatchException`[C], `AccountIdentityChange`[C]
- `Basis/Accounts/AccountSessionManager.cs`: `AccountSessionManager`[CMD]
- `Basis/Accounts/KoLLoginClient.cs`: `KoLLoginResult`[D], `KoLLoginException`[D], `KoLLoginClient`[CMD]
- `Basis/Accounts/KoLPwdHashResolver.cs`: `KoLPwdHashResolver`[CD], `KoLPlayerIdentity`[C]
- `Basis/KoLUserAgent.cs`: `KoLUserAgent`[C]
- `Basis/Security/KoLSecurityPolicy.cs`: `KoLHtmlResponseKind`[D], `KoLHtmlResponseException`[MD]
- `Lib/KolibriClient.cs`: `KolibriLoginRequiredException`[M], `KolibriClient`[CM]

### Nicht von Apps genutzt

- `Basis/Accounts/AccountModels.cs`: `AccountConfiguration` (Tests), `IAccountStore` (Tests), `ISecretStore` (Tests), `IClock` (Tests), `SystemClock` (Lib-intern)
- `Basis/Accounts/FileSecretStore.cs`: `FileSecretStore` (Tests)
- `Basis/Accounts/JsonFileAccountStore.cs`: `JsonFileAccountStore` (Tests)
- `Basis/Accounts/KeyProtection.cs`: `IKeyProtector` (Tests), `NoProtection` (Tests), `OsKeyProtector` (Lib-intern)
- `Basis/Accounts/KoLLoginClient.cs`: `KoLLoginOptions` (tot), `KoLLoginFailureReason` (tot)
- `Basis/Accounts/SessionHealthEvaluator.cs`: `KoLSessionHealthEvaluator` (Tests)
- `Basis/Accounts/SessionHealthModels.cs`: `KoLSessionHealthState` (Tests), `KoLServerResponseKind` (Tests), `KoLServerResponse` (Tests), `KoLSessionObservation` (Tests), `KoLSessionRetryPolicy` (Tests), `KoLSessionHealth` (Lib-intern)
- `Basis/Security/KoLAllowlistHandler.cs`: `KoLAllowlistHandler` (Tests)
- `Basis/Security/KoLSecurityPolicy.cs`: `KoLHostAllowlist` (tot), `KoLSecurityPolicy` (Tests), `KoLSecurityException` (Tests), `KoLHtmlResponseClassifier` (Tests)
- `Lib/KolibriClient.cs`: `MemeShopStepResult` (tot), `MemeShopRunResult` (tot)

## Roher Seitenabruf über Limiter

### Von Apps genutzt

- `Basis/KoLRateLimiter.cs`: `KoLLimiterOptions`[D], `KoLRateLimiter`[D]

### Nicht von Apps genutzt

- `Basis/KoLRateLimiter.cs`: `KoLRequestLane` (Tests), `KoLRequestLanes` (Tests), `IKoLLimiterClock` (Tests), `KoLRateLimitedHandler` (Tests)
- `Basis/Requests/HttpClientKoLRequestTransport.cs`: `HttpClientKoLRequestTransport` (tot)
- `Basis/Requests/KoLRequestContracts.cs`: `KoLResponseKind` (Tests), `KoLRequestMethods` (tot), `KoLRequest` (Tests), `KoLRequestRetryPolicy` (Tests), `KoLRequestOrchestratorOptions` (Tests), `KoLSessionHealthPolicyAdapter` (Tests), `KoLResponse` (Tests), `IKoLRequestTransport` (Lib-intern), `DelegateKoLRequestTransport` (Lib-intern), `KoLRequestResult` (Lib-intern), `KoLResponseResultExtensions` (tot)
- `Basis/Requests/KoLRequestCoordination.cs`: `KoLCircuitBreakerOptions` (Tests), `KoLCircuitState` (Tests), `KoLCircuitOpenException` (Tests), `KoLCircuitBreaker` (Lib-intern), `KoLGlobalAbort` (Tests), `KoLRequestAbortedException` (Tests), `IKoLAccountMutationLane` (tot), `KoLAccountMutationLane` (tot), `KoLAccountMutationLanePool` (Lib-intern)
- `Basis/Requests/KoLRequestOrchestrator.cs`: `KoLRequestOrchestrator` (Tests)
- `Basis/Requests/KoLResponseClassifier.cs`: `KoLResponseClassifier` (Tests)
- `Spiel/Client/KoLRoutes.cs`: `KoLRoutes` (Tests), `AutomationPolicy` (Lib-intern)

## Status/Inventar

### Von Apps genutzt

- `Spiel/Client/AccountReadModels.cs`: `KoLStatusSnapshot`[CMD], `KoLInventoryItem`[C], `KoLInventorySnapshot`[CMD], `IAccountReadClient`[CMD], `KoLAccountReadClient`[CMD]
- `Spiel/Client/CharacterModifierCalculator.cs`: `CharacterModifierCalculator`[D]
- `Spiel/Data/BundledItemNames.cs`: `BundledItemNames`[CM]
- `Spiel/Data/ItemClassifier.cs`: `ItemOrgan`[D], `ItemEquipSlot`[D], `ItemCategory`[D], `ItemOrganCost`[D], `ItemClassification`[D], `ItemClassifier`[D]
- `Spiel/Data/KolMafiaDataRecords.cs`: `MafiaMonsterRecord`[CD], `MafiaItemRecord`[CD], `MafiaSkillRecord`[D], `MafiaAdventureRecord`[D], `MafiaConcoctionRecord`[C], `KoLmafiaCatalog`[CD]
- `Spiel/Data/MafiaDataLoader.cs`: `MafiaDataLoader`[CD]

### Nicht von Apps genutzt

- `Spiel/Client/AccountReadModels.cs`: `KoLTimedEffect` (Tests), `AccountReadPolicy` (Tests)
- `Spiel/Client/CharacterModifierCalculator.cs`: `CharacterModifierResult` (tot), `CharacterGearSuggestion` (tot)
- `Spiel/Client/InventoryStateContracts.cs`: `InventoryBucket` (Tests), `KoLInventoryBucket` (tot), `KoLInventoryStateItem` (tot), `InventoryCostBasis` (tot), `InventoryCostBasisEntry` (tot), `InventoryCostBasisLedger` (tot), `InventoryStateAdapterOptions` (Tests), `KoLInventoryBuckets` (tot), `InventoryBuckets` (tot), `KoLInventoryStateSnapshot` (tot), `IInventoryStateReadClient` (tot), `IInventoryStateReader` (tot), `KoLInventoryStateReadClient` (Tests), `KoLInventoryStateAdapter` (Tests), `AccountReadInventoryStateExtensions` (tot), `InventoryStateAdapter` (tot)
- `Spiel/Client/ItemMaster.cs`: `ItemAccessFlags` (tot), `ItemEligibilityFlags` (tot), `ItemVariantKind` (tot), `ItemSourceTag` (Lib-intern), `ItemVariantLink` (Lib-intern), `ItemMasterRecord` (Tests), `ItemEligibility` (tot), `ItemMaster` (Tests), `KoLmafiaItemsTxtParserOptions` (Tests), `ItemMasterParseResult` (tot), `KoLmafiaItemsTxtParser` (Tests), `ItemMasterParser` (tot), `KoLmafiaItemParser` (tot)
- `Spiel/Client/ItemMetadata.cs`: `ItemMetadata` (tot), `ItemMetadataPolicy` (tot), `IItemMetadataClient` (tot), `KoLItemMetadataClient` (tot)
- `Spiel/Client/OrganLimitDeriver.cs`: `OrganLimitConfidence` (Tests), `OrganLimits` (tot), `OrganLimitDeriver` (Tests)
- `Spiel/Data/ItemClassifier.cs`: `ItemEquipRequirement` (tot)
- `Spiel/Data/KolMafiaDataRecords.cs`: `MafiaZoneRecord` (Lib-intern), `MafiaEffectRecord` (Lib-intern), `MafiaModifierRecord` (Tests), `MafiaMonsterPartRecord` (Lib-intern), `MafiaRestoreRecord` (Lib-intern), `MafiaOrganRecord` (Lib-intern), `MafiaEquipmentRecord` (Lib-intern), `MafiaOutfitRecord` (Lib-intern)
- `Spiel/Data/MafiaDataLoader.cs`: `LoadDiagnostics` (tot), `MafiaMonsterDrop` (Lib-intern)

## Konsum/Rest/Cast/Equip

### Von Apps genutzt

- `Lib/CharacterSession.cs`: `CharacterSession`[D]
- `Spiel/Adventure/RecoveryModels.cs`: `RecoveryConsumable`[M], `RecoveryEffectRequirement`[M], `RecoverySnapshot`[CM], `RecoveryPolicy`[CM], `RecoveryActionKind`[M], `RecoveryStopReason`[M], `RecoveryPlanStatus`[M], `RecoveryPlanStep`[M], `RecoveryPlan`[CM]
- `Spiel/Adventure/RecoveryPlanner.cs`: `RecoveryPlanner`[C]
- `Spiel/Client/CharacterActionClient.cs`: `CharacterEquipmentSlot`[D], `CharacterAction`[D], `UseInventoryItem`[CD], `EquipInventoryItem`[D], `UnequipInventoryItem`[D], `WearCharacterOutfit`[D], `SelectCharacterFamiliar`[D], `EquipCharacterFamiliar`[D], `CharacterConsumableKind`[CMD], `ConsumeCharacterItem`[D], `CastCharacterSkill`[D], `RestAtCampground`[D], `MoveClosetItem`[D], `PullStorageItem`[D], `CombineCharacterItems`[D], `FriarBlessing`[D], `RequestFriarBlessing`[D], `PerformFriarRitual`[D], `FriarsPageState`[D], `CharacterMoodRule`[D], `CharacterStorageSnapshot`[D], `CharacterActionSnapshot`[D], `CharacterCastOutcome`[D], `CharacterActionOutcomeUnknownException`[D], `CharacterRestOutcome`[D], `CharacterActionClient`[CD]
- `Spiel/Client/CharacterRecipeCatalog.cs`: `CharacterRecipeCatalog`[D]

### Nicht von Apps genutzt

- `Spiel/Adventure/AccountRecoverySnapshotProvider.cs`: `IRecoverySnapshotProvider` (tot), `AccountRecoverySnapshotProvider` (Tests)
- `Spiel/Adventure/RecoveryModels.cs`: `RecoveryEffect` (tot), `RecoveryFamiliar` (tot), `RecoveryEquipment` (tot), `RecoveryItem` (tot), `RecoveryConsumableSpec` (tot), `RecoveryActionType` (tot), `RecoveryStep` (tot)
- `Spiel/Adventure/RecoveryPlanner.cs`: `RecoveryPlanRequest` (tot)
- `Spiel/Client/CharacterActionClient.cs`: `CharacterMoodResult` (Lib-intern)
- `Spiel/Client/CharacterRecipeCatalog.cs`: `CharacterRecipe` (tot)

## Daily

### Von Apps genutzt

- `Spiel/Adventure/DailyAdventurePlanExecutor.cs`: `DailyAdventurePlanExecutionOptions`[M], `DailyAdventureStepExecutionResult`[M], `DailyAdventurePlanExecutor`[M]
- `Spiel/Adventure/DailyAdventurePlanner.cs`: `DailyAdventurePlanner`[CMD]
- `Spiel/Adventure/DailyAdventureScheduler.cs`: `DailyAdventureSchedulerOptions`[CM], `DailyAdventureScheduleTick`[CM], `DailyAdventurePlanScheduler`[CM]
- `Spiel/Daily/AccountDailyState.cs`: `AccountDailyState`[C], `IAccountDailyStateReader`[C]
- `Spiel/Daily/ArbitrageRule.cs`: `ArbitrageRule`[C]
- `Spiel/Daily/AutoCombatDailyBridge.cs`: `AutoCombatDailyBridge`[CMD], `BridgeExecutionResult`[CD], `BridgeCompleteness`[CD]
- `Spiel/Daily/BuyPlanner.cs`: `BuyRule`[D], `BuyPlanner`[CD]
- `Spiel/Daily/CampfireLeavesPlanner.cs`: `CampfireLeavesSnapshot`[CM], `CampfireLeavesPlanner`[CM]
- `Spiel/Daily/DailyDoneStore.cs`: `DailyDoneStore`[CMD]
- `Spiel/Daily/DailyInternetShopPlanner.cs`: `InternetShopSnapshot`[C], `DailyInternetShopPlanner`[CD]
- `Spiel/Daily/DailyOrchestrator.cs`: `DailyOrchestrationResult`[C], `RoleOverride`[C], `DailyOrchestrator`[C]
- `Spiel/Daily/DailyRoutine.cs`: `DailyRoutineSnapshot`[CMD], `DailyRoutineReport`[C], `DailyRoutineRunner`[CM]
- `Spiel/Daily/DailyRoutineExecutionResult.cs`: `DailyRoutineExecutionResult`[C]
- `Spiel/Daily/DropTracker.cs`: `QuestDrop`[D]
- `Spiel/Daily/FreePullDispatcher.cs`: `FreePullDispatcher`[CD]
- `Spiel/Daily/HermitPlanner.cs`: `HermitSnapshot`[CM], `HermitPlanner`[CM]
- `Spiel/Daily/LiveDailySnapshotBuilder.cs`: `LiveDailySnapshotBuilder`[CMD], `Ids`[C], `LiveDailySnapshot`[D]
- `Spiel/Daily/MemeShopPage.cs`: `MemeShopCatalog`[C], `MemeShopPage`[C]
- `Spiel/Daily/PriceScanner.cs`: `MallListingRow`[D]
- `Spiel/Daily/StoreLister.cs`: `StoreLister`[C]

### Nicht von Apps genutzt

- `Spiel/Adventure/AdventureDailyContracts.cs`: `IAdventureDailyLimitProvider` (tot), `IAdventureDailyCounterStore` (tot), `InMemoryAdventureDailyCounterStore` (tot), `AdventureDailyCounterCheck` (tot)
- `Spiel/Adventure/DailyAdventurePlanExecutor.cs`: `DailyAdventurePlanExecutionStopReason` (Tests), `DailyAdventurePlanExecutionResult` (tot)
- `Spiel/Daily/AccountDailyState.cs`: `AccountDailyStateReader` (Lib-intern)
- `Spiel/Daily/ArbitragePlanner.cs`: `MarketKind` (Lib-intern), `MarketListing` (Lib-intern), `PriceHistory` (Lib-intern), `ArbitrageOpportunity` (tot), `ArbitrageVerdict` (tot), `ArbitragePlanner` (tot)
- `Spiel/Daily/AutoCombatDailyBridge.cs`: `DailyDecision` (tot)
- `Spiel/Daily/AutoCombatPolicyBuilder.cs`: `AutoCombatPolicyBuilder` (tot)
- `Spiel/Daily/BurningLeavesPage.cs`: `BurningLeavesPage` (Tests)
- `Spiel/Daily/BuyPlanner.cs`: `BuyCandidate` (Tests), `BuyPlan` (tot)
- `Spiel/Daily/CampfireLeavesPlanner.cs`: `CampfireLeavesPlan` (Lib-intern), `CampfireLeavesPlanStep` (tot), `CampfireLeavesPlannerOptions` (tot)
- `Spiel/Daily/DailyInternetShopPlanner.cs`: `InternetShopFreePull` (tot), `InternetShopPlan` (Lib-intern), `InternetShopPlanStep` (tot), `DailyInternetShopPlannerOptions` (tot)
- `Spiel/Daily/DailyOrchestrator.cs`: `RoleDailyPlan` (tot), `DailyOrchestrationReport` (tot)
- `Spiel/Daily/DailyRoutine.cs`: `DailyRoutineCounts` (tot), `ForeignBribePreview` (tot), `DailyRoutineRunnerOptions` (tot)
- `Spiel/Daily/DailyRoutineExecutionResult.cs`: `DailyStepOutcome` (Lib-intern)
- `Spiel/Daily/DropTracker.cs`: `DropTracker` (Lib-intern)
- `Spiel/Daily/EncounterChoiceReader.cs`: `EncounterChoice` (tot), `EncounterChoiceReader` (Lib-intern)
- `Spiel/Daily/FleaMarketReader.cs`: `FleaMarketReader` (tot)
- `Spiel/Daily/FreePullDispatcher.cs`: `ItemIds` (Lib-intern), `FreePullResult` (Lib-intern)
- `Spiel/Daily/HermitPlanner.cs`: `HermitPlan` (Lib-intern), `HermitPlanStep` (tot), `HermitPlannerOptions` (tot)
- `Spiel/Daily/MemeShopPage.cs`: `MemeShopRow` (tot)
- `Spiel/Daily/PriceScanner.cs`: `PriceScanner` (Tests)
- `Spiel/Daily/QuestRunner.cs`: `GrindStep` (tot), `GrindRunResult` (tot), `QuestRunner` (tot)
- `Spiel/Daily/RestockPlanner.cs`: `RestockCandidate` (Tests), `RestockSnapshot` (Tests), `RestockPlan` (Lib-intern), `RestockPlanner` (Tests)
- `Spiel/Daily/StoreLister.cs`: `StoreItemOption` (tot), `ListItemResult` (Lib-intern), `OwnListing` (tot)

## Abenteuer/Kampf/Choice

### Von Apps genutzt

- `Lib/ManualAdventureSession.cs`: `ManualAdventureSession`[D]
- `Spiel/Adventure/AccountAdventureConditionSnapshotProvider.cs`: `AccountAdventureConditionSnapshotProvider`[CM]
- `Spiel/Adventure/AdventureActionAllowlist.cs`: `AdventureActionAllowlist`[CMD]
- `Spiel/Adventure/AdventureConditionContracts.cs`: `AdventureConditionKind`[D], `AdventureCondition`[CMD], `AdventureConditionSnapshot`[CMD], `AdventureConditionResult`[CMD], `AdventureConditionEvaluator`[CMD]
- `Spiel/Adventure/AdventureExecutionContracts.cs`: `AdventureHttpTransportException`[D], `AdventureStateSnapshot`[D], `AdventureApprovalDecision`[CD], `IAdventureApprovalHook`[D], `DelegateAdventureApprovalHook`[CD], `AdventureVerificationResult`[CD], `IAdventureVerificationHook`[D], `DelegateAdventureVerificationHook`[CD], `AdventureExecutionResult`[CMD]
- `Spiel/Adventure/AdventureExecutionGateway.cs`: `AdventureExecutionGateway`[CMD]
- `Spiel/Adventure/AdventureKernelModels.cs`: `AdventureAbortReason`[CD], `AdventureDailyLimitContract`[CMD], `AdventureCounterContract`[D], `AdventureRunRequest`[CMD], `AdventureKernelObservation`[C], `AdventureFightRunState`[C], `AdventureAbortedRunState`[C], `AdventureKernelAction`[CD], `AdventureEncounterAction`[D], `AdventureFightAction`[CD], `AdventureChoiceAction`[CD], `AdventureKernelDecision`[C], `AdventureCheckpoint`[CD], `IAdventureRunCheckpointStore`[M], `InMemoryAdventureRunCheckpointStore`[M]
- `Spiel/Adventure/AdventureLocationContracts.cs`: `AdventureEncounterMetadata`[D], `AdventureLocation`[D], `AdventureLocationCatalog`[C]
- `Spiel/Adventure/AdventureLocationIds.cs`: `AdventureLocationIds`[CM]
- `Spiel/Adventure/AdventureModels.cs`: `AdventureEncounterKind`[CD], `AdventureEncounterState`[CD], `AdventureState`[CMD], `AdventurePlannerOptions`[CMD], `AdventureStep`[M], `DailyAdventurePlan`[MD]
- `Spiel/Adventure/AdventureRunKernel.cs`: `AdventureRunKernel`[C]
- `Spiel/Adventure/AutoCombatAdventureAdapter.cs`: `AutoCombatAdventureAdapter`[MD]
- `Spiel/Adventure/AutoCombatPolicy.cs`: `AutoCombatActionKind`[CD], `AutoCombatAction`[CD], `AutoCombatPredicate`[CD], `AutoCombatContext`[CMD], `AutoCombatUsage`[CMD], `AutoCombatBudget`[C], `AutoCombatRule`[CD], `AutoCombatRuleEvaluation`[D], `AutoCombatDecision`[MD], `AutoCombatPolicy`[CMD]
- `Spiel/Adventure/BoundedAdventureRunner.cs`: `BoundedAdventureRunnerOptions`[CMD], `IAdventureConditionSnapshotProvider`[MD], `BoundedAdventureRunStopReason`[CMD], `BoundedAdventureRunResult`[CMD], `BoundedAdventureRunner`[CMD]
- `Spiel/Adventure/BoundedChoiceChainRunner.cs`: `BoundedChoiceChainOptions`[CMD], `BoundedChoiceChainStopReason`[CMD], `BoundedChoiceChainResult`[MD], `BoundedChoiceChainRunner`[CMD]
- `Spiel/Adventure/ChoiceEngine.cs`: `Choice`[CMD]
- `Spiel/Adventure/ChoiceResolution.cs`: `ChoiceResolutionMode`[C], `ChoiceDefaultEntry`[C], `ChoiceResolutionConfig`[C], `ChoiceResolutionConfigStore`[CMD], `ChoiceOptionInfo`[D], `ChoiceResolutionRequest`[D], `TurtleTamerEncounters`[C], `IChoiceResolver`[D], `ChoiceResolver`[CMD], `NeedsDecisionInfo`[CMD]
- `Spiel/Adventure/CombatObservationModels.cs`: `CombatActionKind`[CMD], `CombatOutcome`[CMD], `CombatAction`[MD], `CombatEffect`[M], `CombatObservation`[MD]
- `Spiel/Adventure/IAdventureExecutionGatewayFactory.cs`: `IAdventureExecutionGatewayFactory`[MD], `IOpenChoiceProbe`[M], `TokenAdventureApprovalHook`[M], `KnownEncounterVerificationHook`[M]
- `Spiel/Adventure/KoLAdventureEncounterParser.cs`: `KoLAdventureEncounterParser`[C]
- `Spiel/Adventure/KoLAdventureSessionFactory.cs`: `KoLAdventureSessionFactory`[CMD], `KoLAdventureSession`[CM]
- `Spiel/Adventure/KoLCombatObservationParser.Support.cs`: `KoLCombatObservationParser`[C]
- `Spiel/Adventure/KoLCombatObservationParser.cs`: `KoLCombatObservationParser`[C]
- `Spiel/Adventure/KoLQuestAccess.cs`: `KoLQuestAccess`[D]
- `Spiel/Adventure/LiveManualCombatTransport.cs`: `LiveManualCombatTransport`[D]
- `Spiel/Adventure/ManualCombatContracts.cs`: `ManualCombatActionKind`[C], `ManualCombatAction`[C], `ManualCombatOutcome`[C], `ManualCombatObservation`[C], `ManualCombatAbortReason`[C], `ManualCombatRequest`[C], `ManualCombatPreview`[C], `ManualCombatApprovalDecision`[D], `DelegateManualCombatApprovalHook`[D]
- `Spiel/Adventure/ManualCombatGateway.cs`: `ManualCombatGateway`[D]
- `Spiel/Adventure/OpenChoice.cs`: `OpenChoiceKind`[CMD], `OpenChoiceDetection`[CMD], `OpenChoiceException`[MD], `OpenChoiceSubmitResult`[D], `OpenChoiceClient`[C]
- `Spiel/Adventure/UnknownPageArchive.cs`: `UnknownPageArchive`[CD]
- `Spiel/Daily/AutoCombatRunner.cs`: `AutoCombatRound`[D], `AutoCombatRunner`[D]
- `Spiel/Data/AdventureManualDriver.cs`: `ManualStepOutcome`[D], `AdventureManualDriver`[D]
- `Spiel/Data/AdventureStepSpec.cs`: `AdventureStepSpec`[CD], `AttackStepSpec`[D], `SkillStepSpec`[D], `ItemStepSpec`[CMD], `RunStepSpec`[D], `ChoiceStepSpec`[CMD], `AutoCombatStepSpecMapper`[D]

### Nicht von Apps genutzt

- `Lib/ManualAdventureSession.cs`: `ManualAdventureBatchResult` (tot)
- `Spiel/Adventure/AdventureActionAllowlist.cs`: `AdventureFightActionRule` (tot), `AdventureChoiceActionRule` (tot), `AdventureAllowlistEntry` (tot)
- `Spiel/Adventure/AdventureCheckpointExtensions.cs`: `AdventureCheckpointExtensions` (tot)
- `Spiel/Adventure/AdventureConditionContracts.cs`: `AdventureConditionOperator` (Tests)
- `Spiel/Adventure/AdventureExecutionContracts.cs`: `AdventureTransportOperation` (Tests), `AdventureTransportKind` (tot), `AdventureHttpRoutes` (Lib-intern), `AdventureHttpRouteMap` (Lib-intern), `AdventureRouteMap` (tot), `AdventureTransportRouteMap` (tot), `AdventureTransportAccountBinding` (Tests), `AdventureAccountBinding` (tot), `AdventureHttpAccountBinding` (tot), `AdventureResponseTooLargeException` (Tests), `AdventureHttpTransportOptions` (Tests), `AdventureTransportOptions` (tot), `KoLAdventureHttpOptions` (tot), `AdventureExecutionHttpOptions` (tot), `HttpAdventureTransportOptions` (tot), `HttpAdventureExecutionTransportOptions` (tot), `AdventureExecutionStatus` (Tests), `AdventureTransportRequest` (Tests), `AdventureTransportResponse` (Tests), `IAdventureExecutionTransport` (Lib-intern), `IAdventureTransport` (Lib-intern), `IAdventureHttpTransport` (tot), `IAdventureExecutionHttpTransport` (Lib-intern), `AdventureApprovalContext` (Lib-intern), `IAdventureExecutionApprovalHook` (tot), `AdventureVerificationContext` (Lib-intern), `IAdventureExecutionVerificationHook` (tot)
- `Spiel/Adventure/AdventureExecutionFixtureTransport.cs`: `AdventureExecutionFixtureTransport` (Tests), `FixtureAdventureTransport` (tot)
- `Spiel/Adventure/AdventureHttpFixtureTransport.cs`: `AdventureFixtureHttpRequest` (tot), `AdventureHttpFixtureTransport` (Tests), `FixtureAdventureHttpTransport` (tot), `KoLAdventureHttpFixtureTransport` (tot), `AdventureExecutionHttpFixtureTransport` (tot)
- `Spiel/Adventure/AdventureKernelModels.cs`: `AdventureRunPhase` (Tests), `AdventureRunStateKind` (tot), `AdventureKernelActionKind` (Tests), `AdventureRunState` (Lib-intern), `AdventureReadyRunState` (Lib-intern), `AdventureEncounterRunState` (Lib-intern), `AdventureChoiceRunState` (Lib-intern), `AdventureRewardRunState` (Lib-intern), `AdventureCompleteRunState` (Lib-intern), `IAdventureRunCheckpointCasStore` (Lib-intern), `IAdventureCheckpointCasStore` (tot), `IAdventureExecutionCheckpointStore` (Lib-intern), `AdventureCheckpointCasResult` (Lib-intern)
- `Spiel/Adventure/AdventureLocationContracts.cs`: `AdventureLocationRequirementKind` (Tests), `AdventureLocationUnlockState` (Tests), `AdventureLocationRequirement` (Tests), `IAdventureLocationCatalog` (tot), `ILocationCatalog` (tot), `LocationCatalog` (tot), `AdventureLocationRunRequest` (Tests)
- `Spiel/Adventure/AdventureModels.cs`: `AdventureActionKind` (Tests), `AdventureExecutionPhase` (Tests), `AdventureExecutionState` (Tests), `IAdventureStateReader` (tot), `IAdventureCheckpointStore` (tot), `DailyAdventurePlanStatus` (Tests)
- `Spiel/Adventure/AutoCombatAdventureAdapter.cs`: `AutoCombatAdventureDecision` (tot)
- `Spiel/Adventure/AutoCombatPolicy.cs`: `AutoCombatActionType` (tot), `AutoCombatObservation` (tot), `AutoCombatState` (tot), `AutoCombatDecisionStatus` (Tests), `AutoCombatJson` (tot), `AutoCombatPolicyEvaluator` (tot), `AutoCombatPolicyDsl` (tot), `AutoCombatEvaluator` (tot)
- `Spiel/Adventure/BoundedManualCombatRunner.cs`: `BoundedManualCombatRunnerOptions` (Tests), `BoundedManualCombatStopReason` (Tests), `BoundedManualCombatResult` (tot), `BoundedManualCombatRunner` (Tests)
- `Spiel/Adventure/ChoiceEngine.cs`: `ChoiceDecisionKind` (Tests), `ChoiceDecisionReason` (Tests), `ChoiceHiddenField` (Tests), `ChoiceHiddenFieldValue` (tot), `ChoiceHiddenFieldValues` (tot), `ChoiceOption` (Tests), `ChoiceDefinition` (Tests), `ChoiceRule` (tot), `ChoiceOptionRule` (tot), `ChoiceHiddenFieldRule` (tot), `ChoiceObservation` (Tests), `ChoicePage` (Tests), `ChoiceInput` (tot), `ChoiceContext` (tot), `ChoicePolicy` (Tests), `ChoiceDecision` (tot), `ChoiceEngine` (Tests)
- `Spiel/Adventure/ChoiceResolution.cs`: `ChoiceResolutionSource` (Tests), `ChoiceResolution` (Tests), `ChoiceResolutionLogEntry` (tot), `BuiltInChoiceRule` (Tests), `NeedsDecisionExtensions` (tot)
- `Spiel/Adventure/CombatObservationModels.cs`: `CombatMonster` (Lib-intern), `CombatResource` (tot)
- `Spiel/Adventure/HttpAdventureExecutionTransport.cs`: `HttpAdventureExecutionTransport` (Tests), `AdventureHttpTransport` (tot), `KoLAdventureHttpTransport` (tot), `AdventureExecutionHttpTransport` (tot), `KoLAdventureExecutionHttpTransport` (tot), `HttpKoLAdventureTransport` (tot)
- `Spiel/Adventure/IAdventureExecutionGatewayFactory.cs`: `TransportAdventureExecutionGatewayFactory` (Tests)
- `Spiel/Adventure/JsonFileAdventureRunCheckpointStore.cs`: `JsonFileAdventureRunCheckpointStore` (Tests)
- `Spiel/Adventure/KoLAdventureEncounterParser.cs`: `IAdventureEncounterParser` (Lib-intern)
- `Spiel/Adventure/KoLCombatObservationParser.cs`: `ICombatObservationParser` (Lib-intern)
- `Spiel/Adventure/KoLQuestAccess.cs`: `KoLQuestProgress` (Tests), `KoLQuestLogSnapshot` (Tests), `KoLLocationAccess` (Lib-intern)
- `Spiel/Adventure/LiveManualCombatTransport.cs`: `QuestPage` (tot)
- `Spiel/Adventure/ManualCombatContracts.cs`: `ManualCombatActionType` (tot), `ManualAttackAction` (Lib-intern), `ManualSkillAction` (Lib-intern), `ManualItemAction` (Lib-intern), `ManualRunAction` (Lib-intern), `AttackCombatAction` (tot), `SkillCombatAction` (tot), `ItemCombatAction` (tot), `RunCombatAction` (tot), `ManualCombatStatus` (Tests), `ManualCombatPolicy` (Lib-intern), `ManualCombatInspectRequest` (Lib-intern), `ManualCombatInspectionResult` (Lib-intern), `ManualCombatExecutionResult` (Lib-intern), `ManualCombatTransportRequest` (Lib-intern), `ManualCombatTransportResponse` (Lib-intern), `IManualCombatTransport` (Lib-intern), `ManualCombatApprovalContext` (Lib-intern), `IManualCombatApprovalHook` (Lib-intern), `IManualCombatGateway` (Lib-intern), `ManualCombatCheckpoint` (Lib-intern), `IManualCombatCheckpointStore` (Lib-intern), `IManualCombatCheckpointCasStore` (Lib-intern), `ManualCombatCheckpointCasResult` (tot), `InMemoryManualCombatCheckpointStore` (Tests), `IManualCombatApprovalLedger` (tot)
- `Spiel/Adventure/ManualCombatFixtureTransport.cs`: `ManualCombatTransportOperation` (Tests), `ManualCombatFixtureTransport` (Tests), `FixtureManualCombatTransport` (tot)
- `Spiel/Adventure/ManualCombatGateway.cs`: `ManualCombatApprovalResult` (tot), `ManualCombatLedgerPayload` (tot)
- `Spiel/Adventure/OpenChoice.cs`: `OpenChoiceState` (Tests), `OpenChoiceRedirect` (Tests)
- `Spiel/Adventure/TurnCounterContracts.cs`: `TurnCounterKind` (Tests), `CounterGate` (Tests), `TurnSnapshot` (Tests), `AccountTurnSnapshot` (tot), `TurnCounter` (Tests), `TurnCounterCasResult` (tot), `TurnSnapshotCasResult` (tot), `ITurnSnapshotReader` (tot), `IReadOnlyCounterStore` (tot), `IReadOnlyTurnCounterStore` (tot), `ITurnCounterReader` (tot), `ICounterStore` (tot), `ITurnCounterStore` (tot), `ITurnSnapshotStore` (tot), `CounterStore` (Tests), `InMemoryCounterStore` (Tests), `InMemoryTurnCounterStore` (tot), `TurnCounterContractExtensions` (tot)
- `Spiel/Daily/AutoCombatBatchRunner.cs`: `AutoCombatAccountPlan` (tot), `AutoCombatAccountOutcome` (tot), `AutoCombatBatchResult` (tot), `AutoCombatBatchRunner` (tot)
- `Spiel/Daily/AutoCombatRunner.cs`: `AutoCombatRunResult` (tot)

## Mall-Suche/-Kauf

### Von Apps genutzt

- `Markt/Mall/HttpMallPurchaseTransport.cs`: `HttpMallPurchaseTransportOptions`[D], `HttpMallPurchaseTransport`[D]
- `Markt/Mall/MallBuyClient.cs`: `MallBuyClient`[CD]
- `Markt/Mall/MallModels.cs`: `MallListing`[MD], `MallLimitKind`[D], `MallItemOffers`[D], `MallSearchPage`[D], `MallSearchParseException`[D], `MallSnapshot`[CMD], `OwnShopListing`[CD], `MallReadPolicy`[D], `IMallReadClient`[CMD], `KoLMallReadClient`[CMD]
- `Markt/Mall/MallPurchaseGateway.cs`: `MallPurchaseGateway`[D]
- `Markt/Mall/MallPurchaseGatewayContracts.cs`: `MallPurchaseOffer`[D], `MallPurchaseAccountState`[D], `MallPurchaseObservation`[D], `MallPurchaseRequest`[D], `MallPurchasePolicy`[D], `MallPurchaseAbortReason`[D], `MallPurchaseVerificationStatus`[D], `MallPurchasePreview`[D], `MallPurchaseTransportCommit`[D], `MallPurchaseCommitResult`[D], `IMallPurchaseTransport`[D], `IAccountExecutionLane`[D]
- `Markt/Mall/PricegunModels.cs`: `PricegunSnapshot`[C], `PricegunReadPolicy`[D], `PricegunRateLimit`[D], `PricegunReadClient`[CMD]
- `Markt/Pricing/IPriceProvider.cs`: `IPriceProvider`[M]
- `Markt/Pricing/MarketPriceEstimator.cs`: `MarketPriceEstimatorOptions`[CM], `MarketEstimate`[C], `MarketPriceEstimator`[C]
- `Markt/Pricing/PriceSnapshot.cs`: `PriceSnapshot`[CM]
- `Markt/Pricing/PricingEngine.cs`: `PricingEngine`[M]
- `Markt/Pricing/PricingModels.cs`: `PriceSummary`[M]

### Nicht von Apps genutzt

- `Markt/Mall/HttpMallPurchaseTransport.cs`: `MallPurchaseHttpResponseClassification` (Tests), `MallPurchaseHttpResponse` (tot), `IMallPurchaseFreshReadHook` (tot), `IMallPurchaseVerificationHook` (tot), `MallPurchaseHttpException` (tot), `KoLMallPurchaseTransport` (tot)
- `Markt/Mall/MallBuyClient.cs`: `MallBuyResult` (Lib-intern)
- `Markt/Mall/MallModels.cs`: `KoLMallPriceProvider` (Lib-intern)
- `Markt/Mall/MallPurchaseFixtureTransport.cs`: `FixtureMallPurchaseTransport` (Tests), `MallPurchaseFixtureTransport` (Tests)
- `Markt/Mall/MallPurchaseGateway.cs`: `MallPurchaseVerifier` (tot)
- `Markt/Mall/MallPurchaseGatewayContracts.cs`: `MallPurchaseRefreshResult` (Lib-intern), `MallPurchaseTransportReceipt` (Tests), `MallPurchaseVerificationResult` (Lib-intern), `IAccountLane` (tot), `SerialAccountLane` (Tests), `SerialAccountLanePool` (Lib-intern), `IMallPurchaseGateway` (Lib-intern)
- `Markt/Mall/MallPurchaseReceiptReader.cs`: `MallReceiptKind` (Tests), `MallReceiptReading` (tot), `MallPurchaseReceiptReader` (Tests), `MallPurchaseDiagnostics` (Tests)
- `Markt/Mall/MallPurchaseSettlement.cs`: `MallPurchaseSettlementKind` (Lib-intern), `MallPurchaseSettlementResult` (tot), `MallPurchaseSettlement` (Lib-intern)
- `Markt/Mall/PricegunModels.cs`: `PricegunSale` (Tests), `PricegunPriceProvider` (Tests)
- `Markt/Pricing/IPriceProvider.cs`: `InMemoryPriceProvider` (Tests)
- `Markt/Pricing/MarketPriceEstimator.cs`: `MarketEstimatorOptions` (tot), `MarketEstimator` (tot), `PriceSnapshotEstimator` (tot), `SaleProceeds` (tot)
- `Markt/Pricing/PriceCache.cs`: `CachedPrices` (Lib-intern), `PriceCache` (Lib-intern)
- `Markt/Pricing/PricePolicy.cs`: `PricePolicy` (Tests)
- `Markt/Pricing/PriceSource.cs`: `PriceSource` (Tests), `QuoteSide` (Tests), `PriceObservation` (Tests)
- `Markt/Pricing/PricingModels.cs`: `PricingRecommendation` (Lib-intern)

## Eigener Shop

### Von Apps genutzt

- `Markt/Mall/ShopMutationModels.cs`: `ShopPriceUpdate`[CM], `KoLShopMutationClient`[C]
- `Markt/Mall/ShopSalesModels.cs`: `ShopSalesSnapshot`[M], `KoLShopSalesReadClient`[C], `ShopBuyerAnalyticsOptions`[C], `ShopBuyerAnalytics`[C]

### Nicht von Apps genutzt

- `Markt/Mall/ShopMutationModels.cs`: `ShopPriceUpdateStatus` (Tests), `ShopPriceUpdateResult` (Lib-intern), `ShopMutationResult` (Lib-intern)
- `Markt/Mall/ShopSalesModels.cs`: `ShopSale` (Tests), `ShopSalesReadPolicy` (tot), `IShopSalesReadClient` (Tests), `BuyerIdentityHasher` (Lib-intern), `ShopSalesLogParser` (tot), `ShopBuyerProfile` (tot), `ShopBuyerLinkCandidate` (tot), `ShopBuyerAnalyticsReport` (tot)
- `Markt/Mall/ShopStocker.cs`: `ShopAddOutcome` (Lib-intern), `ShopStocker` (Tests)

## Pirate-Keys

### Von Apps genutzt

- `Spiel/Client/PirateKeyClient.cs`: `PirateKeyCounts`[C], `PirateKeyRunResult`[C], `PirateKeyClient`[C]

### Nicht von Apps genutzt

- `Spiel/Client/PirateKeyClient.cs`: `ItemChoiceOption` (tot), `ItemChoiceForm` (Tests), `PirateKeyStep` (tot)

## Kmail

### Von Apps genutzt

- `Spiel/Client/KoLMailbox.cs`: `KoLMailEntry`[D], `KoLMailboxSnapshot`[D], `KoLMailFolder`[D], `KoLMailAttachment`[CMD], `KoLMailbox`[CD], `KoLMailSendOutcome`[CM]

### Nicht von Apps genutzt

- (keine)

## Relay

### Von Apps genutzt

- `Basis/Relay/RelayHost.cs`: `RelayHost`[CD]
- `Basis/Relay/RelayServer.cs`: `RelayServer`[D]

### Nicht von Apps genutzt

- `Basis/Relay/RelayServer.cs`: `RelayOptions` (Tests)

## Extras außerhalb der Lib (Arbitrage, CRM, Analytics, Clan, Chat, Coinmaster …)

### Von Apps genutzt

- `Markt/Analytics/InventoryAnalytics.cs`: `InventoryItemSnapshot`[C], `InventoryCategorization`[C], `InventoryCategorizer`[C]
- `Markt/Arbitrage/ItemArbitrageExecutor.cs`: `LiveArbitrageAccountReads`[C], `ItemArbitrageExecutor`[C]
- `Markt/Arbitrage/ItemArbitragePlanner.cs`: `ArbitrageLimits`[CM], `ItemArbitrageSettings`[CM]
- `Markt/Arbitrage/ItemArbitrageService.cs`: `ItemArbitrageService`[CM]
- `Markt/Mall/BaconArbitrageExecutor.cs`: `BaconArbitrageExecutor`[CD], `Ids`[C], `Outcome`[CMD]
- `Markt/Mall/BuyerCrmModels.cs`: `BuyerCrmProfile`[M], `BuyerCrmSyncResult`[M], `BuyerCrmService`[CM], `BuyerCrmStore`[CM]
- `Markt/Mall/CloverRestockExecutor.cs`: `CloverRestockExecutor`[C], `Outcome`[CMD]
- `Markt/Mall/ForeignBribeModels.cs`: `ForeignBribeCatalog`[CD], `ForeignBribePlanner`[C]
- `Markt/Mall/MarketScanner.cs`: `MarketScanner`[C]
- `Markt/Pricing/UnderpricedPurchaseModels.cs`: `UnderpricedPurchaseListing`[CM], `RobustPriceReference`[CM], `UnderpricedItemTradeability`[CM], `UnderpricedPurchaseLimits`[CM]
- `Markt/Pricing/UnderpricedPurchasePlanner.cs`: `UnderpricedPurchasePlanner`[CM]
- `Markt/Safety/JsonFileSellerBlacklistStore.cs`: `JsonFileSellerBlacklistStore`[C]
- `Markt/Safety/SellerStoreBlacklist.cs`: `SellerBlacklistMatcher`[C]
- `Spiel/Client/KoLChat.cs`: `KoLChatMessage`[D], `KoLChatBatch`[D], `KoLChatContact`[D]
- `Spiel/Client/KoLClanLounge.cs`: `KoLClanLoungeFacility`[D], `KoLClanLoungeSnapshot`[D], `KoLCrimboTreeResult`[D], `KoLClanKlawResult`[D], `KoLClanPoolStance`[D], `KoLClanPoolResult`[D], `KoLClanShowerTemperature`[D], `KoLClanShowerResult`[D], `KoLClanSwimWorkout`[D], `KoLClanSwimResult`[D], `KoLClanPoolMove`[D], `KoLClanPoolChoice`[D]
- `Spiel/Client/KoLClanProfile.cs`: `KoLClanProfile`[D], `KoLClanMember`[D]
- `Spiel/Client/KoLClanStash.cs`: `KoLClanStashItem`[D], `KoLClanStashSnapshot`[D]
- `Spiel/Client/MafiaReadScript.cs`: `MafiaReadScript`[C]
- `Spiel/Client/WorkflowModels.cs`: `KoLWorkflowSnapshot`[CM], `KoLWorkflowPlanner`[CM]

### Nicht von Apps genutzt

- `Markt/Analytics/InventoryAgingModels.cs`: `InventoryAgeBucket` (Lib-intern), `InventoryAction` (Lib-intern), `InventoryAgingItem` (Lib-intern), `InventoryVelocity` (Lib-intern), `InventoryAgingResult` (Lib-intern), `InventoryAgingOptions` (Lib-intern), `InventoryExcessPlan` (Lib-intern)
- `Markt/Analytics/InventoryAgingPlanner.cs`: `InventoryAgingPlanner` (Lib-intern), `InventoryExcessPlanner` (tot), `InventoryAgingAnalyzer` (tot), `InventoryPlanner` (tot), `ExcessInventoryPlanner` (tot)
- `Markt/Analytics/InventoryAnalytics.cs`: `InventoryCategory` (Tests), `PricingAction` (Tests), `InventoryAnalyticsOptions` (tot)
- `Markt/Analytics/InventoryVelocity.cs`: `InventoryVelocityCalculator` (Lib-intern)
- `Markt/Arbitrage/ItemArbitrageExecutor.cs`: `ArbitrageStepStatus` (Tests), `ArbitrageStepResult` (tot), `ArbitrageRunResult` (tot), `IArbitrageAccountReads` (tot)
- `Markt/Arbitrage/ItemArbitragePlanner.cs`: `ItemArbitrageInputs` (Tests), `ArbitrageSkipReason` (Tests), `ArbitrageStep` (Tests), `ArbitrageSkip` (tot), `ItemArbitragePlan` (Tests), `ItemArbitragePlanner` (Tests)
- `Markt/Mall/BuyerCrmModels.cs`: `BuyerCrmDatabase` (Tests), `BuyerLoyaltyPolicy` (tot), `BuyerContactAction` (Tests), `BuyerContactIntent` (tot)
- `Markt/Mall/CoinmasterCatalogSource.cs`: `CoinmasterCatalogManifest` (Tests), `CoinmasterCatalogSnapshot` (Tests), `ICoinmasterCatalogSource` (tot), `CoinmasterCatalogSource` (Tests), `CoinmasterCatalogSourceAdapter` (tot), `CoinmasterCatalogSnapshotStore` (tot), `CoinmasterCatalogSourceExtensions` (tot), `ICoinmasterCatalogImporter` (tot), `CoinmasterCatalogImporter` (Tests)
- `Markt/Mall/CoinmasterGraphModels.cs`: `CoinmasterCatalogEntry` (Tests), `CoinmasterCatalogRecord` (Lib-intern), `CoinmasterTicketCost` (tot), `CoinmasterCatalog` (Tests), `AirportCatalog` (tot), `CoinmasterConversionEdge` (Lib-intern), `CoinmasterConversionGraph` (Tests), `AirportConversionGraph` (tot), `CoinmasterGraph` (tot)
- `Markt/Mall/CoinmasterRoiPlanner.cs`: `CoinmasterResaleObservation` (tot), `CoinmasterResale` (tot), `CoinmasterRoiOptions` (Tests), `CoinmasterRoiPlan` (tot), `CoinmasterRoiResult` (tot), `CoinmasterRoiPlanner` (Tests), `AirportRoiPlanner` (tot)
- `Markt/Mall/ForeignBribeModels.cs`: `ForeignBribeCatalogEntry` (Lib-intern), `ForeignBribeRanking` (Lib-intern), `ForeignBribeCostOptions` (tot), `ForeignBribeRoiOptions` (tot)
- `Markt/Mall/IotMModels.cs`: `IotMItemKind` (tot), `IotMRecommendation` (Tests), `IotMAction` (tot), `IotMDecision` (tot), `IotMCatalogEntry` (Tests), `IotMCatalogSnapshot` (Lib-intern), `IIotMCatalogSource` (Lib-intern), `IotMCatalogSnapshotStore` (tot), `IotMMarketSnapshot` (Tests), `MrAccessoryBenchmark` (Tests), `IotMLiquidity` (Tests), `IotMUtilityNpv` (Lib-intern), `IotMScannerOptions` (Tests), `IotMScanResult` (Lib-intern)
- `Markt/Mall/IotMScanner.cs`: `IotMUtilityValuation` (tot), `MrAccessoryBenchmarkCalculator` (tot), `IotMLiquidityCalculator` (tot), `IotMScanner` (Tests)
- `Markt/Mall/MarketScanner.cs`: `MarketSnapshot` (tot)
- `Markt/Mall/MrStoreCatalog.cs`: `MrStoreCatalogManifest` (Tests), `MrStoreCatalogSnapshot` (Tests), `IMrStoreCatalogSource` (Lib-intern), `MrStoreCatalogSource` (Tests), `MrStoreCatalogSourceAdapter` (tot), `MrStoreCatalogSourceExtensions` (tot)
- `Markt/Pricing/FarmRoiModels.cs`: `FarmDrop` (tot), `FarmTargetResource` (Lib-intern), `FarmConsumableCost` (tot), `FarmZone` (Lib-intern), `FarmRoiRequest` (Lib-intern), `FarmVsBuyRequest` (tot), `FarmRoiDecision` (Lib-intern), `FarmVsBuyDecision` (tot), `FarmRoiResult` (Lib-intern)
- `Markt/Pricing/FarmVsBuyPlanner.cs`: `FarmRoiPlannerOptions` (tot), `FarmVsBuyPlannerOptions` (tot), `FarmVsBuyPlanner` (Lib-intern), `FarmRoiPlanner` (tot)
- `Markt/Pricing/RepricePlanner.cs`: `RepriceRecommendation` (tot), `RepricePlan` (tot), `RepricePlanRequest` (tot), `RepricePlanner` (Tests), `RobustRepricePlanner` (tot)
- `Markt/Pricing/RepricePolicy.cs`: `RepricePolicy` (Tests), `RobustRepricePolicy` (tot), `RepricePolicyOptions` (tot), `RepriceReasonCode` (Tests), `RepriceReason` (tot), `RepriceRecommendationStatus` (Tests), `RepriceAction` (Lib-intern)
- `Markt/Pricing/RotatingSales.cs`: `RotatingSaleCandidate` (tot), `RotatingSalePolicy` (tot), `RotatingSaleStatus` (tot), `RotatingSaleProposal` (tot), `RotatingSalePlan` (tot), `RotatingSalePlanner` (tot)
- `Markt/Pricing/UnderpricedPurchaseModels.cs`: `UnderpricedPurchaseRejectionReason` (Lib-intern), `UnderpricedPurchaseProposal` (Lib-intern), `UnderpricedPurchasePlan` (Lib-intern), `UnderpricedPurchasePlanRequest` (Lib-intern), `UnderpricedReference` (tot), `ItemTradeability` (tot), `UnderpricedPurchasePolicy` (tot), `UnderpricedPurchasePlannerOptions` (tot), `RobustMarketReference` (tot), `PurchaseListing` (tot), `PurchaseCandidate` (tot)
- `Markt/Safety/JsonFileSellerBlacklistStore.cs`: `JsonFileBlacklistStore` (tot), `SellerBlacklistStore` (tot), `JsonSellerBlacklistStore` (tot)
- `Markt/Safety/OwnStoreExclusionPolicy.cs`: `OwnStoreExclusionDecision` (tot), `OwnStoreExclusionPolicy` (Tests), `OwnStoreExclusion` (Lib-intern), `OwnStorePolicy` (tot)
- `Markt/Safety/SellerStoreBlacklist.cs`: `SellerStoreIdentity` (Tests), `SellerBlacklistMatchKind` (Lib-intern), `SellerBlacklistEntry` (Tests), `SellerBlacklistDatabase` (Tests), `SellerBlacklistMatch` (tot), `ISellerBlacklistStore` (tot), `ISellerBlacklistRepository` (Lib-intern), `SellerBlacklistConfigurationException` (Lib-intern), `SellerBlacklist` (tot), `SellerStoreBlacklist` (tot), `SellerBlacklistService` (Tests)
- `Spiel/Client/WorkflowModels.cs`: `KoLWorkflowAction` (Tests), `KoLWorkflowStepStatus` (Tests), `KoLWorkflowStep` (Tests), `KoLWorkflowPlan` (Tests)
