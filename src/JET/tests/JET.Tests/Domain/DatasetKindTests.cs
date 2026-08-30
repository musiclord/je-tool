using JET.Domain;
using Xunit;

namespace JET.Tests.Domain;

public sealed class DatasetKindTests
{
    [Theory]
    [InlineData(DatasetKind.Gl, "gl")]
    [InlineData(DatasetKind.Tb, "tb")]
    [InlineData(DatasetKind.AccountMapping, "account_mapping")]
    public void ToStorageName_KnownKind_ReturnsStorageName(DatasetKind kind, string expectedStorageName)
    {
        var storageName = kind.ToStorageName();

        Assert.Equal(expectedStorageName, storageName);
    }


    [Fact]
    public void ToStorageName_InvalidKind_ThrowsArgumentOutOfRangeException()
    {
        const DatasetKind invalidKind = (DatasetKind)999;

        var exception = Assert.Throws<ArgumentOutOfRangeException>(() => invalidKind.ToStorageName());

        Assert.Equal("kind", exception.ParamName);
        Assert.Equal(invalidKind, exception.ActualValue);
    }
}
