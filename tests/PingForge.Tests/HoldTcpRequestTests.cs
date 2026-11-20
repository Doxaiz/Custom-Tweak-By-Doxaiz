using PingForge.Probing;
using Xunit;

namespace PingForge.Tests;

/// <summary>
/// Разбор служебного режима «подержать соединение». Режим запускается дочерним процессом, и ошибка в
/// разборе означала бы либо обычный запуск приложения вместо проверки, либо соединение не туда.
/// </summary>
public sealed class HoldTcpRequestTests
{
    [Fact]
    public void Полный_режим_разбирается()
    {
        bool parsed = HoldTcpRequest.TryParse(
            ["--hold-tcp", "140.82.112.3", "80", "20"], out HoldTcpRequest? request);

        Assert.True(parsed);
        Assert.NotNull(request);
        Assert.Equal("140.82.112.3", request!.Address);
        Assert.Equal(80, request.Port);
        Assert.Equal(20, request.Seconds);
    }

    [Fact]
    public void Без_режима_приложение_запускается_как_обычно()
    {
        Assert.False(HoldTcpRequest.TryParse(["--self-check"], out HoldTcpRequest? request));
        Assert.Null(request);
        Assert.False(HoldTcpRequest.TryParse(null, out _));
        Assert.False(HoldTcpRequest.TryParse([], out _));
    }

    [Theory]
    [InlineData("--hold-tcp", "140.82.112.3", "80")]
    [InlineData("--hold-tcp", "140.82.112.3", "0", "20")]
    [InlineData("--hold-tcp", "140.82.112.3", "70000", "20")]
    [InlineData("--hold-tcp", "140.82.112.3", "80", "0")]
    [InlineData("--hold-tcp", "140.82.112.3", "80", "3600")]
    [InlineData("--hold-tcp", "", "80", "20")]
    public void Неполный_или_невозможный_режим_не_принимается(params string[] args)
    {
        Assert.False(HoldTcpRequest.TryParse(args, out HoldTcpRequest? request));
        Assert.Null(request);
    }
}
