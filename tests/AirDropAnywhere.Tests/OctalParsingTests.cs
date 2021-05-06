using System;
using System.Text;
using AirDropAnywhere.Core;
using Xunit;

namespace AirDropAnywhere.Tests
{
    public class OctalParsingTests
    {
        [Theory]
        [InlineData("7645342", 2050786, true)]
        [InlineData("00004567", 2423, true)]
        [InlineData("0", 0, true)]
        [InlineData("", 0, false)] // zero length
        [InlineData("7AB5342", 0, false)] // no numbers
        [InlineData("a string", 0, false)] // no numbers
        [InlineData("-3423423", 0, false)] // parsing to a ulong - sign is not allowed
        public void ParseOctalToNumber(string input, ulong expectedValue, bool success)
        {
            var stringAsByteSpan = Encoding.ASCII.GetBytes(input);
            Assert.Equal(success, Utils.TryParseOctalToUInt32(stringAsByteSpan, out var actualValue));
            Assert.Equal(expectedValue, actualValue);
        }
    }
}