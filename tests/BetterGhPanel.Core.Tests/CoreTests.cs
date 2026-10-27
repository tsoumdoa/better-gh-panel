using Xunit;

// One discoverable test per original executable group; all original assertions remain in place.
public sealed class CoreTests
{
    [Fact] public void StatisticsAndEmptyBranches() => Program.StatisticsAndEmptyBranches();
    [Fact] public void StatisticsChecks() => global::StatisticsChecks.Run();
    [Fact] public void CapturePolicyChecks() => global::CapturePolicyChecks.Run();
    [Fact] public void GeometryQueueChecks() => global::GeometryQueueChecks.Run();
    [Fact] public void NurbsCandidateLimitChecks() => global::NurbsCandidateLimitChecks.Run();
    [Fact] public void SetOccurrencesChecks() => global::SetOccurrencesChecks.Run();
    [Fact] public void DisplaySetChecks() => global::DisplaySetChecks.Run();
    [Fact] public void ItemOrderChecks() => global::ItemOrderChecks.Run();
    [Fact] public void SearchMatchesValuePathAndType() => Program.SearchMatchesValuePathAndType();
    [Fact] public void NumericComparisonsUseInvariantCulture() => Program.NumericComparisonsUseInvariantCulture();
    [Fact] public void BranchSelectionPreservesTreeSemantics() => Program.BranchSelectionPreservesTreeSemantics();
    [Fact] public void TypeAndInvalidFilters() => Program.TypeAndInvalidFilters();
    [Fact] public void StableMixedSorting() => Program.StableMixedSorting();
    [Fact] public void OddLengthSorting() => Program.OddLengthSorting();
    [Fact] public void BranchSortingChecks() => global::BranchSortingChecks.Run();
    [Fact] public void ImmutableSnapshotAndView() => Program.ImmutableSnapshotAndView();
    [Fact] public void EmptyAndNonfiniteNumbers() => Program.EmptyAndNonfiniteNumbers();
    [Fact] public void InvalidFiniteNumbers() => Program.InvalidFiniteNumbers();
    [Fact] public void ExtremeNumericStatistics() => Program.ExtremeNumericStatistics();
    [Fact] public void Cancellation() => Program.Cancellation();
    // The original console runner called this group from a dedicated main thread.
    // Preserve that assumption: Task.Run may otherwise inline on xUnit's pool thread.
    [Fact] public async System.Threading.Tasks.Task BudgetedQueryChecks() =>
        await System.Threading.Tasks.Task.Factory.StartNew(global::BudgetedQueryChecks.Run,
            System.Threading.CancellationToken.None, System.Threading.Tasks.TaskCreationOptions.LongRunning,
            System.Threading.Tasks.TaskScheduler.Default);
    [Fact] public void LargeData() => Program.LargeData();
    [Fact] public void CanvasDimensionsAndPresets() => Program.CanvasDimensionsAndPresets();
    [Fact] public void CanvasMenuPagingAndSnapshot() => Program.CanvasMenuPagingAndSnapshot();
    [Fact] public void CanvasMenuChoiceChecks() => global::CanvasMenuChoiceChecks.Run();
    [Fact] public void PanelRowSelectionChecks() => global::PanelRowSelectionChecks.Run();
    [Fact] public void SelectionPerformanceChecks() => global::SelectionPerformanceChecks.Run();
    [Fact] public void FilterCanvasChecks() => global::FilterCanvasChecks.Run();
    [Fact] public void UnitConversionsAndFormatting() => Program.UnitConversionsAndFormatting();
    [Fact] public void UnitConversionChecks() => global::UnitConversionChecks.Run();
    [Fact] public void UnitMenuLayout() => Program.UnitMenuLayout();
    [Fact] public void GeometryMetricConversions() => Program.GeometryMetricConversions();
    [Fact] public void FilterQueryChecks() => global::FilterQueryChecks.Run();
    [Fact] public void QueryPolicyChecks() => global::QueryPolicyChecks.Run();
}
