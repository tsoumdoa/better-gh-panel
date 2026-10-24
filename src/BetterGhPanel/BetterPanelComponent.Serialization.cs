using GH_IO.Serialization;

namespace BetterGhPanel
{
    public sealed partial class BetterPanelComponent
    {
        private static GH_IReader UpgradeLegacyOutputParameters(GH_IReader reader)
        {
            // Recognize only the known one-input/one-output schema. Missing
            // chunks in a modern or partial archive must still report corruption.
            if (reader.ItemExists("PanelOutputSchema") || reader.ItemExists("PanelOutputScope") ||
                !reader.ChunkExists("param_input", 0) || !reader.ChunkExists("param_output", 0) ||
                reader.ChunkExists("param_input", 1) || reader.ChunkExists("param_output", 1) ||
                reader.ChunkExists("param_output", 2) || reader.ChunkExists("param_output", 3) || reader is not GH_Chunk original)
                return reader;

            // A detached copy preserves D chunks and their wire identities without
            // editing the source archive. Preserve the owner for relative paths
            // and genuine archive diagnostics; loose undo chunks have no owner.
            var upgraded = GH_Chunk.InstantiateRoot(original.Archive ?? new GH_Archive());
            upgraded.CopyValuesFromChunk(original);
            var defaults = new BetterPanelComponent();
            defaults.Params.Input[1].Write(upgraded.CreateChunk("param_input", 1));
            for (int i = 1; i < defaults.Params.Output.Count; i++)
                defaults.Params.Output[i].Write(upgraded.CreateChunk("param_output", i));
            return upgraded;
        }
    }
}
