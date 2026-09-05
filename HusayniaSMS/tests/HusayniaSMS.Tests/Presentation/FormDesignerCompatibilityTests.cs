using System.ComponentModel;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Xml.Linq;
using HusayniaSMS.WinForms.Forms;

namespace HusayniaSMS.Tests.Presentation;

[TestClass]
public sealed class FormDesignerCompatibilityTests
{
    [TestMethod]
    public void ParameterlessFormsConstructDisplayAndDisposeOnSta() =>
        RunSta(() =>
        {
            using var mainForm = new MainForm();
            using var contactDialog = new ContactDialog();

            Assert.IsFalse(GetField<Label>(mainForm, "safeDemoBanner").Visible);
            Assert.IsFalse(contactDialog.OkButton.Enabled);
            Assert.IsNull(contactDialog.ContactResult);

            mainForm.Show();
            contactDialog.Show(mainForm);
            Application.DoEvents();
            Assert.IsTrue(mainForm.Visible);
            Assert.IsTrue(contactDialog.Visible);

            contactDialog.Close();
            mainForm.Close();
            Application.DoEvents();
            Assert.IsFalse(contactDialog.Visible);
            Assert.IsFalse(mainForm.Visible);
        });

    [TestMethod]
    public void RuntimeMainFormConstructorAppliesSafeDemoBannerState() =>
        RunSta(() =>
        {
            using var safeDemo = new MainForm(isSafeDemo: true);
            using var live = new MainForm(isSafeDemo: false);

            safeDemo.Show();
            live.Show();
            Application.DoEvents();

            Assert.IsTrue(GetField<Label>(safeDemo, "safeDemoBanner").Visible);
            Assert.IsFalse(GetField<Label>(live, "safeDemoBanner").Visible);
        });

    [TestMethod]
    public void ParameterlessConstructorsArePublicAndHiddenFromIntelliSense()
    {
        foreach (var formType in new[] { typeof(MainForm), typeof(ContactDialog) })
        {
            var constructor = formType.GetConstructor(Type.EmptyTypes);
            Assert.IsNotNull(constructor, formType.Name);
            Assert.IsTrue(constructor.IsPublic, formType.Name);
            var attribute = constructor.GetCustomAttribute<EditorBrowsableAttribute>();
            Assert.IsNotNull(attribute, formType.Name);
            Assert.AreEqual(EditorBrowsableState.Never, attribute.State, formType.Name);
        }
    }

    [TestMethod]
    public void RecipientGridIsTopLevelAndRetainsDesignerConstructibility()
    {
        var assembly = typeof(MainForm).Assembly;
        var gridType = assembly.GetType(
            "HusayniaSMS.WinForms.Forms.RecipientDataGridView",
            throwOnError: true);

        Assert.IsNotNull(gridType);
        Assert.IsNull(gridType.DeclaringType);
        Assert.IsTrue(gridType.IsPublic);
        Assert.IsTrue(gridType.IsSealed);
        Assert.IsTrue(typeof(DataGridView).IsAssignableFrom(gridType));
        Assert.IsNotNull(gridType.GetConstructor(Type.EmptyTypes));
        var editorBrowsable = gridType.GetCustomAttribute<EditorBrowsableAttribute>();
        Assert.IsNotNull(editorBrowsable);
        Assert.AreEqual(EditorBrowsableState.Never, editorBrowsable.State);
    }

    [TestMethod]
    public void FormsUseStandardPartialFilesResourcesAndProjectNesting()
    {
        var root = FindSolutionRoot();
        var forms = Path.Combine(
            root,
            "src",
            "HusayniaSMS.WinForms",
            "Forms");
        var projectPath = Path.Combine(
            root,
            "src",
            "HusayniaSMS.WinForms",
            "HusayniaSMS.WinForms.csproj");

        foreach (var formName in new[] { "MainForm", "ContactDialog" })
        {
            var runtimePath = Path.Combine(forms, $"{formName}.cs");
            var designerPath = Path.Combine(forms, $"{formName}.Designer.cs");
            var resourcePath = Path.Combine(forms, $"{formName}.resx");
            Assert.IsTrue(File.Exists(runtimePath), runtimePath);
            Assert.IsTrue(File.Exists(designerPath), designerPath);
            Assert.IsTrue(File.Exists(resourcePath), resourcePath);

            var runtimeSource = File.ReadAllText(runtimePath);
            var designerSource = File.ReadAllText(designerPath);
            StringAssert.Contains(runtimeSource, $"partial class {formName}");
            StringAssert.Contains(designerSource, $"partial class {formName}");
            StringAssert.Contains(designerSource, "InitializeComponent()");
            Assert.IsFalse(
                designerSource.Contains("ContactDialogRequest", StringComparison.Ordinal));
            Assert.IsFalse(
                designerSource.Contains("IContactDraftValidator", StringComparison.Ordinal));
            _ = XDocument.Load(resourcePath);
        }

        var contactRuntime = File.ReadAllText(Path.Combine(forms, "ContactDialog.cs"));
        Assert.IsFalse(contactRuntime.Contains("InitializeDialog", StringComparison.Ordinal));
        var mainRuntime = File.ReadAllText(Path.Combine(forms, "MainForm.cs"));
        Assert.IsFalse(
            mainRuntime.Contains("class RecipientDataGridView", StringComparison.Ordinal));
        var gridSource = File.ReadAllText(Path.Combine(forms, "RecipientDataGridView.cs"));
        StringAssert.Contains(gridSource, "public sealed class RecipientDataGridView");

        var project = XDocument.Load(projectPath);
        AssertProjectMetadata(project, "Compile", @"Forms\ContactDialog.cs", "SubType", "Form");
        AssertProjectMetadata(
            project,
            "Compile",
            @"Forms\ContactDialog.Designer.cs",
            "DependentUpon",
            "ContactDialog.cs");
        AssertProjectMetadata(
            project,
            "EmbeddedResource",
            @"Forms\ContactDialog.resx",
            "DependentUpon",
            "ContactDialog.cs");
        AssertProjectMetadata(project, "Compile", @"Forms\MainForm.cs", "SubType", "Form");
        AssertProjectMetadata(
            project,
            "Compile",
            @"Forms\MainForm.Designer.cs",
            "DependentUpon",
            "MainForm.cs");
        AssertProjectMetadata(
            project,
            "EmbeddedResource",
            @"Forms\MainForm.resx",
            "DependentUpon",
            "MainForm.cs");
    }

    [TestMethod]
    public void DesignerComponentContainersUseExplicitNullInitializers()
    {
        var forms = Path.Combine(
            FindSolutionRoot(),
            "src",
            "HusayniaSMS.WinForms",
            "Forms");

        foreach (var formName in new[] { "MainForm", "ContactDialog" })
        {
            var designerSource = File.ReadAllText(
                Path.Combine(forms, $"{formName}.Designer.cs"));
            StringAssert.Contains(
                designerSource,
                "private System.ComponentModel.IContainer? components = null;",
                $"{formName} must remain warning-clean if Visual Studio removes the " +
                "InitializeComponent container assignment during serialization.");
        }
    }

    [TestMethod]
    public void DesignerSourcesAvoidRuntimeLayoutHelpersAndModernCollectionExpressions()
    {
        var forms = Path.Combine(
            FindSolutionRoot(),
            "src",
            "HusayniaSMS.WinForms",
            "Forms");
        var mainDesigner = File.ReadAllText(Path.Combine(forms, "MainForm.Designer.cs"));
        var contactDesigner = File.ReadAllText(Path.Combine(forms, "ContactDialog.Designer.cs"));

        Assert.IsFalse(mainDesigner.Contains("ConfigureButtonSizing", StringComparison.Ordinal));
        Assert.IsFalse(mainDesigner.Contains("var root", StringComparison.Ordinal));
        Assert.IsFalse(mainDesigner.Contains("Controls.AddRange([", StringComparison.Ordinal));
        Assert.IsFalse(mainDesigner.Contains("Items.AddRange([", StringComparison.Ordinal));
        Assert.IsFalse(contactDesigner.Contains("InitializeDialog", StringComparison.Ordinal));
        StringAssert.Contains(contactDesigner, "new ColumnStyle(SizeType.Absolute, 32F)");
        StringAssert.Contains(contactDesigner, "SetIconPadding(nameTextBox, 4)");
        StringAssert.Contains(contactDesigner, "SetIconPadding(numberTextBox, 4)");
    }

    private static void AssertProjectMetadata(
        XDocument project,
        string itemName,
        string update,
        string metadataName,
        string expectedValue)
    {
        var item = project
            .Descendants(itemName)
            .SingleOrDefault(element =>
                string.Equals(
                    (string?)element.Attribute("Update"),
                    update,
                    StringComparison.Ordinal));
        Assert.IsNotNull(item, $"{itemName} Update=\"{update}\"");
        Assert.AreEqual(expectedValue, (string?)item.Element(metadataName));
        Assert.IsNull(item.Attribute("Include"));
    }

    private static T GetField<T>(MainForm form, string name) where T : class
    {
        var field = typeof(MainForm).GetField(
            name,
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.IsNotNull(field);
        return (T)field.GetValue(form)!;
    }

    private static string FindSolutionRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "HusayniaSMS.sln")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        Assert.Fail("Could not locate solution root.");
        return string.Empty;
    }

    private static void RunSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                action();
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }
}
