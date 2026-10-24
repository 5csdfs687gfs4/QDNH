using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using QDNH.Security;
using QDNH.SharedRadio;
using Xunit;
namespace QDNH.Tests;
public class OperatorAuthenticationTests
{
    private const string AliceSecret = "test-fixture-alice-12345";
    private const string BobSecret = "test-fixture-bob-678901";
    private static string AccountJson(string role = "Operator") => JsonSerializer.Serialize(new
    {
        SchemaVersion = 1,
        Operators = new[] {
        new { UserId="alice", Callsign="EA1TEST", Role=role, SecretEnvironmentVariable="QDNH_TEST_ALICE" },
        new { UserId="bob", Callsign="EA2TEST", Role="Observer", SecretEnvironmentVariable="QDNH_TEST_BOB" }
    }
    });
    private static string? Resolve(string name) => name == "QDNH_TEST_ALICE" ? AliceSecret : name == "QDNH_TEST_BOB" ? BobSecret : null;
    private static byte[] Challenge(string secret, byte[] salt) => SHA256.HashData(Encoding.ASCII.GetBytes(secret).Concat(salt).ToArray());
    [Fact]
    public void IndividualSecretAuthenticatesIdentityAndRejectsWrongOrReplayedChallenge()
    {
        var authentication = OperatorAuthentication.Parse(AccountJson(), Resolve);
        byte[] salt = RandomNumberGenerator.GetBytes(32);
        var alice = authentication.Authenticate(Challenge(AliceSecret, salt), salt);
        Assert.Equal("alice", alice!.UserId); Assert.Equal("EA1TEST", alice.Callsign);
        Assert.Null(authentication.Authenticate(Challenge("shared-old-password", salt), salt));
        Assert.Null(authentication.Authenticate(Challenge(AliceSecret, salt), RandomNumberGenerator.GetBytes(32)));
    }
    [Theory]
    [InlineData("Administrator")]
    [InlineData("Operator")]
    public void OperatorAndAdministratorCanControlAndTransmit(string role)
    {
        var policy = OperatorAuthentication.Parse(AccountJson(role), Resolve);
        var salt = new byte[32]; var principal = policy.Authenticate(Challenge(AliceSecret, salt), salt);
        using var manager = new SessionManager(); manager.ResetForReconfigure(true);
        Guid id = Guid.NewGuid(); var frames = new List<byte[]>();
        manager.RegisterOrAttach(id, "SUPLANTADO", false, frames.Add, 2, principal);
        Assert.Equal("EA1TEST", manager.GetOperators().Operators.Single().Callsign);
        Assert.Equal(role, manager.GetOperators().Operators.Single().Role);
        var ack = JsonSerializer.Deserialize<HelloAckMessage>(frames[0].AsSpan(5));
        Assert.Equal("alice", ack!.AuthenticatedUserId); Assert.Equal(role, ack.Role);
        Assert.Contains("operator-auth-v1", ack.Capabilities!);
        manager.TryRequestControl(id); manager.TryRequestTx(id, 145.5, "FM");
        Assert.Equal(id, manager.ControlOwnerSessionId); Assert.Equal(id, manager.TxOwnerSessionId);
    }
    [Fact]
    public void ObserverCanListenButCannotTakeControlQueueOrTx()
    {
        using var manager = new SessionManager(); manager.ResetForReconfigure(true);
        Guid id = Guid.NewGuid(); var observer = new OperatorPrincipal("bob", "EA2TEST", OperatorRole.Observer);
        var frames = new List<byte[]>(); manager.RegisterOrAttach(id, "ADMIN", true, frames.Add, 2, observer);
        manager.AttachAudio(id, (_, _) => { }, observer); manager.TryRequestControl(id); manager.QueueControl(id, true); manager.TryRequestTx(id, 145.5, "FM");
        Assert.Null(manager.ControlOwnerSessionId); Assert.Null(manager.TxOwnerSessionId);
        Assert.Equal(0, manager.GetOperators().Operators.Single().QueuePosition);
        Assert.Contains(frames, f => f[0] == 5 && Encoding.UTF8.GetString(f, 5, f.Length - 5).Contains("ROLE_OBSERVER"));
        Assert.Contains(frames, f => f[0] == 9 && Encoding.UTF8.GetString(f, 5, f.Length - 5).Contains("ROLE_OBSERVER"));
    }
    [Fact]
    public void EveryChannelBindsSessionToAuthenticatedUserAndDisconnectCannotEraseBindingWhileAudioLives()
    {
        using var manager = new SessionManager(); manager.ResetForReconfigure(true);
        var alice = new OperatorPrincipal("alice", "EA1TEST", OperatorRole.Operator);
        var bob = new OperatorPrincipal("bob", "EA2TEST", OperatorRole.Operator);
        Guid id = Guid.NewGuid(); Action<byte[]> control = _ => { }; Action<byte[], int> audio = (_, _) => { };
        manager.AttachAudio(id, audio, alice); manager.RegisterOrAttach(id, "OTHER", false, control, 2, alice);
        Assert.Throws<UnauthorizedAccessException>(() => manager.AttachSerial(id, (_, _) => { }, bob));
        Assert.Throws<UnauthorizedAccessException>(() => manager.AttachAudio(id, (_, _) => { }, bob));
        Assert.Throws<UnauthorizedAccessException>(() => manager.RegisterOrAttach(id, "OTHER", false, _ => { }, 2, bob));
        manager.OnControlChannelClosed(id, control);
        Assert.Throws<UnauthorizedAccessException>(() => manager.RegisterOrAttach(id, "OTHER", false, _ => { }, 2, bob));
        manager.OnAudioChannelClosed(id, audio);
        manager.RegisterOrAttach(id, "BOB", false, _ => { }, 2, bob); // Ya no existe ninguna conexión de Alice con ese GUID.
        Assert.Equal("EA2TEST", manager.GetOperators().Operators.Single().Callsign);
    }
    [Fact]
    public void RequiredAuthenticationFailsClosedForMissingIdentity()
    {
        using var manager = new SessionManager(); manager.ResetForReconfigure(true); Guid id = Guid.NewGuid();
        Assert.Throws<UnauthorizedAccessException>(() => manager.RegisterOrAttach(id, "FAKE", true, _ => { }));
        Assert.Throws<UnauthorizedAccessException>(() => manager.AttachAudio(id, (_, _) => { }));
        Assert.Throws<UnauthorizedAccessException>(() => manager.AttachSerial(id, (_, _) => { }));
        Assert.Throws<UnauthorizedAccessException>(() => manager.RegisterOrAttach(Guid.Empty, "FAKE", true, _ => { }));
    }
    [Theory]
    [InlineData("role")]
    [InlineData("missing-secret")]
    [InlineData("duplicate-secret")]
    [InlineData("unknown-property")]
    [InlineData("duplicate-property")]
    [InlineData("weak-secret")]
    [InlineData("unicode-secret")]
    [InlineData("duplicate-user")]
    [InlineData("duplicate-callsign")]
    [InlineData("wrong-version")]
    public void InvalidConfigurationNeverFallsBackToLegacyAccess(string variant)
    {
        string json = AccountJson(); Func<string, string?> environment = Resolve;
        switch (variant)
        {
            case "role": json = AccountJson("Admin"); break;
            case "missing-secret": environment = _ => null; break;
            case "duplicate-secret": environment = _ => AliceSecret; break;
            case "unknown-property": json = json.Replace("SchemaVersion", "UnknownVersion"); break;
            case "duplicate-property": json = json.Replace("\"SchemaVersion\":1", "\"SchemaVersion\":1,\"SchemaVersion\":1"); break;
            case "weak-secret": environment = _ => "123"; break;
            case "unicode-secret": environment = _ => "secreto-con-eñe-12345"; break;
            case "duplicate-callsign": json = json.Replace("EA2TEST", "ea1test"); break;
            case "duplicate-user": json = json.Replace("\"bob\"", "\"ALICE\""); break;
            case "wrong-version": json = json.Replace("\"SchemaVersion\":1", "\"SchemaVersion\":2"); break;
        }
        Assert.Throws<InvalidDataException>(() => OperatorAuthentication.Parse(json, environment));
    }
    [Fact]
    public void NoConfigurationPreservesLegacyModeButMissingConfiguredFileIsError()
    {
        Assert.Null(OperatorAuthentication.Load(""));
        Assert.Throws<FileNotFoundException>(() => OperatorAuthentication.Load(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json")));
    }
}
