# CIA Alpha regression gate

Run the focused gate with:

```powershell
dotnet test CIA.slnx --filter "TestCategory=AlphaRegressionGate"
```

The full solution suite remains the final regression check. The category selects the smallest
existing focused tests plus the permanent end-to-end fixture tests needed to make each Alpha
acceptance area explicit.

| Acceptance area | Automated evidence |
| --- | --- |
| Load / Source Sets | `FirstSuccessfulLoadCreatesSetOneAndCarriesItsIdentityDownstream`; `ExistingAndNewSetLoadingPreserveNormalActiveSetBehavior`; `LoadSettingsCreateAndRenameKeepTheInternalSetIdentityStable`; `SingleAndBatchReassignmentPreserveSourcesAndInvalidateDownstream` |
| Logical and detailed Discovery identity / stateless preview | `PermanentFixtureDiscoveryKeepsLogicalAndDetailedSetIdentity`; `FreshServiceRetrievesOccurrenceWithoutPriorDiscoveryRun`; `ProblematicSourceDoesNotPreventIndependentSourcesFromAggregating` |
| Four hierarchy layouts | `FourLayoutsPreserveHierarchyBoundariesConflictsAndPhysicalValues` (four data rows); `AlignByPositionUsesBlanksAndDiscardsNoUnequalGroupValues`; `StructuralRowsDoNotPositionallyAssociateIndependentBranches` |
| All-combinations bounds / cancellation | `OversizeAllCombinationsFailsBeforeReturningAnyPartialResult`; `AllCombinationsHonorsCancellationDuringGeneration` |
| File / Set boundaries, conflicts, physical occurrences, provenance | `FourLayoutsPreserveHierarchyBoundariesConflictsAndPhysicalValues` |
| Database metadata and Include / Exclude | `IncludedRowsFlowAtomicallyIntoRoutedWorkbooksWithoutChangingRowTruth`; `InclusionAndValueOrMetadataSearchAreAppliedBeforePaging` |
| Atomic Database replacement | `CancelledCandidatePreservesPreviousPublishedGeneration` |
| Extraction and retained replacement / no XML reread | `IncludedRowsFlowAtomicallyIntoRoutedWorkbooksWithoutChangingRowTruth`; `IncludedHierarchyRowsAreSnapshottedAcrossSetsWithoutRereadingXml`; `FailedCancelledAndIncompleteReplacementRetainPriorPublishedResult` |
| Routed multi-workbook OpenXML output | `IncludedRowsFlowAtomicallyIntoRoutedWorkbooksWithoutChangingRowTruth`; `RoutedBatchStreamsSemanticRowsAcrossWorkbooksAndWorksheets`; `DisabledSetRetainsRoutingFieldsHeadersAndMetadataButProducesNoOutput`; `RoutingAndFieldChangesDoNotMutateDatabaseGeneration` |
| Collision cancel / rename | `CancellingCollisionResolutionStartsNoExportAndChangesNoFile`; `DifferentNameUpdatesSessionDefinitionAndPublishesResolvedPath` |
| Mixed overwrite/new and atomic rollback | `MixedAuthorizedOverwriteRenamedTargetAndNewTargetPublishTogether`; `BatchPublicationRollsBackFilesMovedBeforeARace`; `PublicationFailureRestoresOriginalAndRemovesNewFiles`; `CandidateFailureAndCancellationPublishNoWorkbook` |
| Secure XML / DTD blocking | `MalformedAndProhibitedEntityXmlFailWithoutResolvingExternalContent` |
| Current / Stale workflow and Esc cancellation | `SuccessfulWorkflowTracksCurrentAndCascadingStaleState`; `EscapeFromAWorkspaceCancelsOnlyTheCurrentActiveOperation` |
| Structured history / diagnostics | `DesktopAndProcessingHostWriteCorrelatedStructuredClefRecords` |
| Typed IPC and host lifecycle | `DesktopAndProcessingHostExchangeTypedContractsOverNamedPipe`; `FramerRejectsStructurallyInvalidTypedContract`; `UnexpectedHostTerminationCreatesOneNewCleanReadyHostWithoutWorkloadArguments`; `BatchContractsRoundTripThroughTypedIpc` |
| No legacy ordinal build/export fallback | `LegacyFlatGenerationIsRejectedAndNoLegacyExportStreamIsExposed`; `ServiceExposesNoLegacyFlatOrSingleWorkbookEntryPoint` |

The permanent fixture is under `CIA.ProcessingHost.Tests/Fixtures/AlphaRegression` and contains
three Source Sets, four valid source files, and one independently malformed source. It covers
multi-path logical fields, independent same-name fields across Sets, nested/repeated groups,
unequal lengths, same-content physical occurrences, explicit mapping conflicts, formula-looking
text, preserved whitespace, and multi-file boundaries.
