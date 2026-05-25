using garge_operator.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace garge_operator.Tests;

/// <summary>
/// Pins the startup configuration-validation wiring from Program.cs:
/// <c>AddOptions&lt;T&gt;().Bind(section).ValidateDataAnnotations().ValidateOnStart()</c>.
/// Each test rebuilds that pipeline against an in-memory <see cref="IConfiguration"/> so the
/// DataAnnotations on <see cref="MqttOptions"/> and <see cref="ApiOptions"/> are exercised through
/// the same path the host uses. With ValidateOnStart, missing or blank required keys must surface
/// as an eager <see cref="OptionsValidationException"/> rather than a lazy failure on first use.
/// </summary>
public class OptionsValidationTests
{
    // Mirrors Program.cs: bind both options sections from configuration with DataAnnotation
    // validation enforced at startup, then build the provider so ValidateOnStart can run.
    private static ServiceProvider BuildProvider(IDictionary<string, string?> settings)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(settings)
            .Build();

        var services = new ServiceCollection();

        services.AddOptions<MqttOptions>()
            .Bind(configuration.GetSection(MqttOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.AddOptions<ApiOptions>()
            .Bind(configuration.GetSection(ApiOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        return services.BuildServiceProvider();
    }

    private static Dictionary<string, string?> ValidSettings() => new()
    {
        ["Mqtt:Broker"] = "broker.example.com",
        ["Mqtt:Port"] = "8883",
        ["Mqtt:Username"] = "mqtt-user",
        ["Mqtt:Password"] = "mqtt-pass",
        ["Api:BaseUrl"] = "http://api.example.com",
        ["Api:Email"] = "ops@example.com",
        ["Api:Password"] = "api-pass",
    };

    // Resolving IOptions<T>.Value runs DataAnnotation validation lazily; ValidateOnStart wires the
    // same validation into host startup. Touching .Value here reproduces the validation that
    // ValidateOnStart performs eagerly, without needing to spin up a full host.
    private static T Resolve<T>(ServiceProvider provider) where T : class
        => provider.GetRequiredService<IOptions<T>>().Value;

    // ── All required keys present → resolution succeeds ──────────────────────

    [Fact]
    public void AllKeysPresent_MqttOptions_Resolves()
    {
        using var provider = BuildProvider(ValidSettings());

        var options = Resolve<MqttOptions>(provider);

        Assert.Equal("broker.example.com", options.Broker);
        Assert.Equal(8883, options.Port);
        Assert.Equal("mqtt-user", options.Username);
        Assert.Equal("mqtt-pass", options.Password);
    }

    [Fact]
    public void AllKeysPresent_ApiOptions_Resolves()
    {
        using var provider = BuildProvider(ValidSettings());

        var options = Resolve<ApiOptions>(provider);

        Assert.Equal("http://api.example.com", options.BaseUrl);
    }

    // ── Missing / blank required keys → OptionsValidationException ────────────

    [Theory]
    [InlineData("Mqtt:Broker")]
    [InlineData("Mqtt:Username")]
    [InlineData("Mqtt:Password")]
    public void MqttOptions_RequiredStringMissing_Throws(string keyToRemove)
    {
        var settings = ValidSettings();
        settings.Remove(keyToRemove);
        using var provider = BuildProvider(settings);

        Assert.Throws<OptionsValidationException>(() => Resolve<MqttOptions>(provider));
    }

    [Theory]
    [InlineData("Mqtt:Broker")]
    [InlineData("Mqtt:Username")]
    [InlineData("Mqtt:Password")]
    public void MqttOptions_RequiredStringBlank_Throws(string keyToBlank)
    {
        var settings = ValidSettings();
        settings[keyToBlank] = "";
        using var provider = BuildProvider(settings);

        Assert.Throws<OptionsValidationException>(() => Resolve<MqttOptions>(provider));
    }

    [Fact]
    public void MqttOptions_PortMissing_DefaultsToZero_OutOfRange_Throws()
    {
        // No Mqtt:Port binds the default 0, which is outside the [1, 65535] Range constraint.
        var settings = ValidSettings();
        settings.Remove("Mqtt:Port");
        using var provider = BuildProvider(settings);

        Assert.Throws<OptionsValidationException>(() => Resolve<MqttOptions>(provider));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("65536")]
    public void MqttOptions_PortOutOfRange_Throws(string port)
    {
        var settings = ValidSettings();
        settings["Mqtt:Port"] = port;
        using var provider = BuildProvider(settings);

        Assert.Throws<OptionsValidationException>(() => Resolve<MqttOptions>(provider));
    }

    [Fact]
    public void ApiOptions_BaseUrlMissing_Throws()
    {
        var settings = ValidSettings();
        settings.Remove("Api:BaseUrl");
        using var provider = BuildProvider(settings);

        Assert.Throws<OptionsValidationException>(() => Resolve<ApiOptions>(provider));
    }

    [Fact]
    public void ApiOptions_BaseUrlBlank_Throws()
    {
        var settings = ValidSettings();
        settings["Api:BaseUrl"] = "";
        using var provider = BuildProvider(settings);

        Assert.Throws<OptionsValidationException>(() => Resolve<ApiOptions>(provider));
    }

    [Fact]
    public void ApiOptions_OptionalEmailAndPasswordMissing_StillResolves()
    {
        // Email and Password are optional (nullable, no [Required]); only BaseUrl is mandatory.
        var settings = ValidSettings();
        settings.Remove("Api:Email");
        settings.Remove("Api:Password");
        using var provider = BuildProvider(settings);

        var options = Resolve<ApiOptions>(provider);

        Assert.Equal("http://api.example.com", options.BaseUrl);
        Assert.Null(options.Email);
        Assert.Null(options.Password);
    }
}
