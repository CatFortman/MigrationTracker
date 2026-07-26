using MigrationOps.Core.MigrationFramework.Scripts;

namespace MigrationOps.Core.Tests
{
    public class ComputeChecksumTests
    {
        [Fact]
        public void SameContentProducesSameChecksum()
        {
            var script = "-- Tags: Db1\nSELECT 1;";

            Assert.Equal(ScriptParser.ComputeChecksum(script), ScriptParser.ComputeChecksum(script));
        }

        [Fact]
        public void DifferentContentProducesDifferentChecksum()
        {
            var scriptA = "-- Tags: Db1\nSELECT 1;";
            var scriptB = "-- Tags: Db1\nSELECT 2;";

            Assert.NotEqual(ScriptParser.ComputeChecksum(scriptA), ScriptParser.ComputeChecksum(scriptB));
        }

        [Fact]
        public void IgnoresALeadingChecksumHeaderLine()
        {
            var withHeader = "-- Checksum: whatever-was-here\n-- Tags: Db1\nSELECT 1;";
            var withoutHeader = "-- Tags: Db1\nSELECT 1;";

            Assert.Equal(ScriptParser.ComputeChecksum(withoutHeader), ScriptParser.ComputeChecksum(withHeader));
        }

        [Fact]
        public void HeaderStrippingOnlyAppliesToTheFirstLine()
        {
            var script = "-- Tags: Db1\n-- Checksum: not-a-header-here\nSELECT 1;";

            Assert.Equal(ScriptParser.ComputeChecksum(script), ScriptParser.ComputeChecksum(script));
            Assert.NotEqual(ScriptParser.ComputeChecksum(script), ScriptParser.ComputeChecksum("-- Tags: Db1\nSELECT 1;"));
        }

        [Fact]
        public void HandlesWindowsLineEndings()
        {
            var withHeader = "-- Checksum: abc\r\n-- Tags: Db1\r\nSELECT 1;";
            var withoutHeader = "-- Tags: Db1\r\nSELECT 1;";

            Assert.Equal(ScriptParser.ComputeChecksum(withoutHeader), ScriptParser.ComputeChecksum(withHeader));
        }

        [Fact]
        public void ReturnsSixtyFourCharacterUppercaseHex()
        {
            var checksum = ScriptParser.ComputeChecksum("-- Tags: Db1\nSELECT 1;");

            Assert.Equal(64, checksum.Length);
            Assert.Equal(checksum, checksum.ToUpperInvariant());
        }
    }
}
