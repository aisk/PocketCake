using PocketCake.Core.Agent;

namespace PocketCake.Core.Tests.Agent;

public sealed class AgentAuthTests
{
    private const string Token = "abc123_-XYZ";
    private const string Bearer = "Bearer " + Token;

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("localhost")]
    [InlineData("[::1]")]
    public void AuthorizesLoopbackHostWithTheToken(string host)
    {
        Assert.True(AgentAuth.IsAuthorized(host, null, Bearer, Token));
        Assert.True(AgentAuth.IsAuthorized(host, "", Bearer, Token));
        Assert.True(AgentAuth.IsAuthorized(host, "http://localhost:3000", Bearer, Token));
    }

    [Theory]
    [InlineData("evil.example")]
    [InlineData("192.168.1.10")]
    [InlineData("127.0.0.1.nip.io")]
    public void RefusesNonLoopbackHost(string host)
    {
        Assert.False(AgentAuth.IsLocal(host, null));
        Assert.False(AgentAuth.IsAuthorized(host, null, Bearer, Token));
    }

    [Theory]
    [InlineData("http://evil.example")]
    [InlineData("null")]
    [InlineData("not a url")]
    public void RefusesNonLoopbackOrigin(string origin)
    {
        Assert.False(AgentAuth.IsLocal("127.0.0.1", origin));
        Assert.False(AgentAuth.IsAuthorized("127.0.0.1", origin, Bearer, Token));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Bearer")]
    [InlineData("Bearer ")]
    [InlineData("Bearer wrong")]
    [InlineData("Bearer abc123_-XY")]
    [InlineData("Basic abc123_-XYZ")]
    [InlineData(Token)]
    public void RefusesAMissingOrWrongToken(string? header)
    {
        Assert.True(AgentAuth.IsLocal("127.0.0.1", null));
        Assert.False(AgentAuth.IsAuthorized("127.0.0.1", null, header, Token));
    }

    [Fact]
    public void BearerSchemeIsCaseInsensitive()
    {
        Assert.True(AgentAuth.HasToken("bearer " + Token, Token));
    }

    [Fact]
    public void RefusesEverythingWhenNoTokenIsConfigured()
    {
        Assert.False(AgentAuth.IsAuthorized("127.0.0.1", null, "Bearer ", null));
        Assert.False(AgentAuth.IsAuthorized("127.0.0.1", null, "Bearer ", ""));
    }

    [Fact]
    public void NewTokenIs32RandomBytesBase64Url()
    {
        var a = AgentAuth.NewToken();

        Assert.Equal(43, a.Length);
        Assert.Matches("^[A-Za-z0-9_-]+$", a);
        Assert.NotEqual(a, AgentAuth.NewToken());
    }
}
