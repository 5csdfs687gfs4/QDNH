using System;
using System.Collections.Generic;
using System.Linq;
using QDNH.Audio;
using Xunit;
namespace QDNH.Tests;
public class Pcm16BlockAssemblerTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(17)]
    public void FragmentationAlwaysProducesEvenBlocksWithoutLosingSamples(int size)
    {
        var source = Enumerable.Range(0, 256).Select(i => (byte)i).ToArray();
        var assembler = new Pcm16BlockAssembler(); var output = new List<byte>();
        for (int offset = 0; offset < source.Length; offset += size)
        {
            byte[] part = source.Skip(offset).Take(size).ToArray();
            byte[] complete = assembler.Push(part, part.Length);
            Assert.Equal(0, complete.Length % 2); output.AddRange(complete);
        }
        Assert.Equal(source, output.ToArray());
    }
    [Fact]
    public void NewConnectionCannotReuseHalfSampleFromPreviousConnection()
    {
        var assembler = new Pcm16BlockAssembler(); Assert.Empty(assembler.Push(new byte[] { 9 }, 1));
        assembler.Reset(); Assert.Equal(new byte[] { 1, 2 }, assembler.Push(new byte[] { 1, 2 }, 2));
    }
}
