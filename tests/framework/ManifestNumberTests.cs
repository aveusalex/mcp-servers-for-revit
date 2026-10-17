using RevitMCP.Manifest;
using Xunit;

namespace RevitMCPFramework.Tests
{
    /// <summary>
    /// The capabilityHash is computed over canonical JSON on both sides (C# ManifestGen and the TS server), so
    /// numbers must be written the way JavaScript writes them. Expected strings come from Node's String(number).
    /// </summary>
    public class ManifestNumberTests
    {
        [Theory]
        [InlineData(0.000001d, "0.000001")]
        [InlineData(1e-7d, "1e-7")]
        [InlineData(1e21d, "1e+21")]
        [InlineData(100000000000000000000d, "100000000000000000000")]
        [InlineData(123.456d, "123.456")]
        [InlineData(0.1d, "0.1")]
        [InlineData(-0.5d, "-0.5")]
        [InlineData(1234567.891d, "1234567.891")]
        [InlineData(5e-324d, "5e-324")]
        [InlineData(1.7976931348623157e308d, "1.7976931348623157e+308")]
        [InlineData(0.0001d, "0.0001")]
        [InlineData(0.000025d, "0.000025")]
        [InlineData(100.25d, "100.25")]
        [InlineData(0.3333333333333333d, "0.3333333333333333")]
        public void Doubles_are_written_like_JavaScript(double value, string expected)
        {
            Assert.Equal(expected, ToolManifestBuilder.JsNumber(value));
        }
    }
}
