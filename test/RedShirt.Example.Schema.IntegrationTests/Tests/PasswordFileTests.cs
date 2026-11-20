namespace RedShirt.Example.Schema.IntegrationTests.Tests;

public class PasswordFileTests
{
    [Theory]
    [InlineData("secret", "secret")]
    [InlineData("secret\n", "secret")]
    [InlineData("secret\r\n", "secret")]
    [InlineData("secret\n\n", "secret\n")]
    [InlineData(" sec ret \t", " sec ret \t")]
    [InlineData("p;SslMode=None", "p;SslMode=None")]
    public void Read_DropsOneTrailingLineEnding(string contents, string expected)
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, contents);
            Assert.Equal(expected, PasswordFile.Read(path));
        }
        finally
        {
            File.Delete(path);
        }
    }
}