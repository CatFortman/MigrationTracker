using MigrationOps.Core.MigrationFramework.Services;

namespace MigrationOps.Core.Tests
{
    public class ComputeChecksumTests
    {
        [Fact]
        public void SameContentProducesSameChecksum()
        {
            var script = "-- Tags: Db1\nSELECT 1;";

            Assert.Equal(MigrationService.ComputeChecksum(script), MigrationService.ComputeChecksum(script));
        }

        [Fact]
        public void DifferentContentProducesDifferentChecksum()
        {
            var scriptA = "-- Tags: Db1\nSELECT 1;";
            var scriptB = "-- Tags: Db1\nSELECT 2;";

            Assert.NotEqual(MigrationService.ComputeChecksum(scriptA), MigrationService.ComputeChecksum(scriptB));
        }

        [Fact]
        public void IgnoresALeadingChecksumHeaderLine()
        {
            var withHeader = "-- Checksum: whatever-was-here\n-- Tags: Db1\nSELECT 1;";
            var withoutHeader = "-- Tags: Db1\nSELECT 1;";

            Assert.Equal(MigrationService.ComputeChecksum(withoutHeader), MigrationService.ComputeChecksum(withHeader));
        }

        [Fact]
        public void HeaderStrippingOnlyAppliesToTheFirstLine()
        {
            var script = "-- Tags: Db1\n-- Checksum: not-a-header-here\nSELECT 1;";

            Assert.Equal(MigrationService.ComputeChecksum(script), MigrationService.ComputeChecksum(script));
            Assert.NotEqual(MigrationService.ComputeChecksum(script), MigrationService.ComputeChecksum("-- Tags: Db1\nSELECT 1;"));
        }

        [Fact]
        public void HandlesWindowsLineEndings()
        {
            var withHeader = "-- Checksum: abc\r\n-- Tags: Db1\r\nSELECT 1;";
            var withoutHeader = "-- Tags: Db1\r\nSELECT 1;";

            Assert.Equal(MigrationService.ComputeChecksum(withoutHeader), MigrationService.ComputeChecksum(withHeader));
        }

        [Fact]
        public void ReturnsSixtyFourCharacterUppercaseHex()
        {
            var checksum = MigrationService.ComputeChecksum("-- Tags: Db1\nSELECT 1;");

            Assert.Equal(64, checksum.Length);
            Assert.Equal(checksum, checksum.ToUpperInvariant());
        }
    }
}
