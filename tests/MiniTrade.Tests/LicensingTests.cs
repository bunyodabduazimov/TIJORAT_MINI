using System.Security.Cryptography;
using MiniTrade.Licensing;

namespace MiniTrade.Tests;

public class LicensingTests
{
    private static LicensePayload Payload(DateTime? exp = null) => new()
    {
        LicenseKey = LicenseKeyGenerator.Generate(), Type = LicenseType.Annual, CustomerName = "Тест",
        InstallationId = "iid", DeviceFingerprint = "fp", IssuedAtUtc = DateTime.UtcNow,
        ExpiresAtUtc = exp, CheckBeforeUtc = DateTime.UtcNow.AddDays(30)
    };

    [Fact]
    public void Token_roundtrip_and_tamper_detection()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var pub = LicenseTokenCodec.LoadPublicKey(key.ExportSubjectPublicKeyInfoPem());
        var token = LicenseTokenCodec.Sign(Payload(), key);

        Assert.True(LicenseTokenCodec.TryVerify(token, pub, out var p));
        Assert.Equal("Тест", p!.CustomerName);

        var tampered = (token[0] == 'a' ? 'b' : 'a') + token[1..];
        Assert.False(LicenseTokenCodec.TryVerify(tampered, pub, out _));

        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Assert.False(LicenseTokenCodec.TryVerify(LicenseTokenCodec.Sign(Payload(), other), pub, out _));
        Assert.False(LicenseTokenCodec.TryVerify("garbage", pub, out _));
    }

    [Fact]
    public void Evaluator_checks_device_expiry_offline_period_and_clock()
    {
        var now = DateTime.UtcNow;
        Assert.True(LicenseEvaluator.Evaluate(Payload(), "iid", "fp", now, null).IsUsable);
        Assert.Equal(LicenseCheckResult.WrongDevice, LicenseEvaluator.Evaluate(Payload(), "iid", "other", now, null).Result);
        Assert.Equal(LicenseCheckResult.Expired, LicenseEvaluator.Evaluate(Payload(now.AddDays(-1)), "iid", "fp", now, null).Result);
        Assert.Equal(LicenseCheckResult.OfflinePeriodExceeded, LicenseEvaluator.Evaluate(Payload(), "iid", "fp", now.AddDays(31), null).Result);
        Assert.Equal(LicenseCheckResult.ValidNeedsRefresh, LicenseEvaluator.Evaluate(Payload(), "iid", "fp", now.AddDays(25), null).Result);
        Assert.Equal(LicenseCheckResult.ClockTampered, LicenseEvaluator.Evaluate(Payload(), "iid", "fp", now, now.AddDays(2)).Result);
        Assert.Equal(LicenseCheckResult.NoLicense, LicenseEvaluator.Evaluate(null, "iid", "fp", now, null).Result);
    }

    [Fact]
    public void Key_generator_produces_well_formed_unique_keys()
    {
        var keys = Enumerable.Range(0, 200).Select(_ => LicenseKeyGenerator.Generate()).ToList();
        Assert.All(keys, k => Assert.True(LicenseKeyGenerator.IsWellFormed(k)));
        Assert.Equal(200, keys.Distinct().Count());
        Assert.True(LicenseKeyGenerator.IsWellFormed(" mt-abcde-fghjk-mnpqr-stuvw "));
        Assert.False(LicenseKeyGenerator.IsWellFormed("XX-123"));
    }
}
