using HusayniaSMS.Core.Batching;
using HusayniaSMS.Core.Contacts;
using HusayniaSMS.Core.Messaging;
using HusayniaSMS.Core.Settings;
using HusayniaSMS.WinForms.Forms;
using HusayniaSMS.WinForms.Infrastructure.Csv;
using HusayniaSMS.WinForms.Infrastructure.Settings;
using HusayniaSMS.WinForms.Infrastructure.Twilio;
using HusayniaSMS.WinForms.Presentation;
using HusayniaSMS.WinForms.SafeDemo;

namespace HusayniaSMS.WinForms;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        var parsed = StartupOptionsParser.Parse(args);
        if (!parsed.Succeeded || parsed.Options is null)
        {
            MessageBox.Show(parsed.SafeError ?? "Invalid startup options.",
                "Husaynia SMS startup error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        var safeDemo = parsed.Options.Mode == StartupMode.SafeDemo;
        var pathProvider = new LocalSettingsPathProvider(safeDemo);
        var phoneValidator = new E164PhoneNumberValidator();
        var contactValidator = new ContactRowValidator(phoneValidator);
        var draftValidator = new ContactDraftValidator(phoneValidator);
        var setupValidator = new SetupValidator(phoneValidator);
        var settingsStore = new JsonLocalSettingsStore(pathProvider.SettingsPath);
        var settingsService = new SettingsService(
            settingsStore, new DpapiSecretProtector(), setupValidator);
        var csvStore = new CsvHelperContactCsvStore(contactValidator);
        ITwilioTransportFactory transportFactory = safeDemo
            ? new ScriptedFakeTwilioTransportFactory(parsed.Options.Scenario!.Value)
            : new TwilioTransportFactory();
        var form = new MainForm(safeDemo);
        var controller = new MainController(
            form,
            new WinFormsUserDialogs(form),
            csvStore,
            settingsService,
            new MessageValidator(),
            new BatchSendCoordinator(transportFactory),
            TimeProvider.System,
            safeDemo,
            draftValidator,
            contactValidator);
        form.AttachController(controller);
        Application.Run(form);
    }
}