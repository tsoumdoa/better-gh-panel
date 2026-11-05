// Pure menu policy uses full names; SDK label integration is exercised in Rhino.
namespace BetterGhPanel.UI
{
    internal sealed partial class CanvasMenu
    {
        internal static partial string ShortTypeName(string name) => name;
    }
}
