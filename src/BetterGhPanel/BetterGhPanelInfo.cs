using System;
using System.Drawing;
using System.Reflection;
using Grasshopper.Kernel;

namespace BetterGhPanel
{
    public sealed class BetterGhPanelInfo : GH_AssemblyInfo
    {
        public override string Name => "better-gh-panel";
        public override string Description => "Fast tree inspection, filtering, sorting and statistics for Grasshopper.";
        public override Bitmap Icon => PanelIcon.Create();
        public override Guid Id => new Guid("C7C9B948-DDA8-4729-90EA-71EB09C1A0B2");
        public override string AuthorName => "AEC Tooling";
        public override string AuthorContact => "https://github.com/tsoumdoa/better-gh-panel";
        public override string AssemblyVersion => GetType().Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion.Split('+')[0] ?? "0.1.0";
    }
}
