using HusayniaSMS.Core.Messaging;
using HusayniaSMS.Core.Settings;

namespace HusayniaSMS.WinForms.SafeDemo;

public sealed class ScriptedFakeTwilioTransportFactory(SafeDemoScenario scenario)
    : ITwilioTransportFactory
{
    public ITwilioTransport Create(TwilioCredentials credentials) =>
        new ScriptedFakeTwilioTransport(scenario);
}
