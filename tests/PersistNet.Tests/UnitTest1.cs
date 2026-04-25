using Xunit;

namespace PersistNet.Tests;

public class UnitTest1
{
    [Fact]
    public void DummyTest_Passes()
    {
        // Arrange
        int expected = 42;

        // Act
        int actual = 42;

        // Assert
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void AnotherDummyTest_Also_Passes()
    {
        // Arrange
        string message = "PersistNet";

        // Act
        bool hasContent = !string.IsNullOrEmpty(message);

        // Assert
        Assert.True(hasContent);
    }
}
