# HusayniaSMS Designer-Editable Forms Architecture

Date: 2026-09-04  
Status: Proposed; advisory only  
Scope: Make `ContactDialog` and `MainForm` round-trip safely through the Visual Studio WinForms
Designer and prevent clipped contact-validation indicators. No application source is changed by
this design.

## 1. Current state

- **FACT DS-01:** Production composition creates `MainForm(bool)` and attaches `MainController`;
  no form constructs services or transports (`HusayniaSMS/src/HusayniaSMS.WinForms/Program.cs:28-52`).
- **FACT DS-02:** `ContactDialog` is one non-partial, sealed code file. Its only constructor
  requires `ContactDialogRequest` and `IContactDraftValidator`, and it creates all controls in
  `InitializeDialog` (`HusayniaSMS/src/HusayniaSMS.WinForms/Forms/ContactDialog.cs:6-24,35-122`).
- **FACT DS-03:** `ContactDialog` has a 460x165 client area and 420x205 minimum outer size. Its
  two-column layout docks each text box through the right edge, while `ErrorProvider` uses default
  icon placement; no right-side space is reserved (`ContactDialog.cs:44-62,71-88,109-115`).
- **FACT DS-04:** Validation strings remain available through `ErrorProvider.SetError`, OK is
  disabled until valid, and current tests depend on the exact messages and internal control
  accessors (`ContactDialog.cs:27-33,148-167`;
  `HusayniaSMS/tests/HusayniaSMS.Tests/Presentation/ContactDialogTests.cs:21-35`).
- **FACT DS-05:** `MainForm` is partial and has a `.Designer.cs`/`.resx`, but its only constructor
  is `MainForm(bool isSafeDemo)` (`HusayniaSMS/src/HusayniaSMS.WinForms/Forms/MainForm.cs:8-25`).
- **FACT DS-06:** `MainForm.Designer.cs` creates many layout controls and four grid columns as
  local/anonymous objects rather than fields. It also calls a handwritten button helper from
  `InitializeComponent` (`MainForm.Designer.cs:47-113,123-156,158-194,210-253,260-269`).
- **FACT DS-07:** The designer-instantiated grid type is an `internal` class nested inside
  `MainForm`, although `MainForm.Designer.cs` constructs it directly
  (`MainForm.Designer.cs:25,67`; `MainForm.cs:388-469`).
- **FACT DS-08:** The SDK-style WinForms project targets `net8.0-windows` with
  `UseWindowsForms=true`, but contains no explicit `SubType` or `DependentUpon` item metadata
  (`HusayniaSMS/src/HusayniaSMS.WinForms/HusayniaSMS.WinForms.csproj:1-14`).
- **FACT DS-09:** `MainForm.resx` is a valid minimal form resource and tests have internal access to
  WinForms members (`MainForm.resx:1-7`;
  `HusayniaSMS/src/HusayniaSMS.WinForms/Properties/AssemblyInfo.cs:1-3`).
- **FACT DS-10:** Existing displayed STA tests construct `MainForm(true)` and exercise DPI/font
  sizing; `ContactDialog` tests construct only the runtime dependency-taking constructor
  (`ContactEditingWinFormsTests.cs:38-75,426-451`;
  `ContactDialogTests.cs:118-171`).
- **FACT DS-11:** A controller-less `MainForm` currently cancels closing because
  `MainForm_FormClosing` sets `e.Cancel` before checking `_controller`
  (`MainForm.cs:473-485`).

## 2. Desired state

Both forms use the normal three-file Visual Studio shape:

```text
Forms/ContactDialog.cs
  Forms/ContactDialog.Designer.cs
  Forms/ContactDialog.resx

Forms/MainForm.cs
  Forms/MainForm.Designer.cs
  Forms/MainForm.resx
```

`InitializeComponent` owns controls, layout, static properties, and event subscriptions. Runtime
constructors own only runtime state: safe-demo visibility, contact mode/title/button wording,
initial values, validator/request assignment, and first validation.

The contact dialog reserves a scaled right gutter for both ErrorProvider icons, is modestly wider,
and retains exact validation text, keyboard mnemonics, accessibility names, and button behavior.

## 3. Binding implementation delta

### 3.1 `ContactDialog.cs` — runtime/logic partial

Change the declaration to:

```csharp
public sealed partial class ContactDialog : Form
```

Use nullable runtime state plus an explicit configured flag; do not create a fake request or fake
validator for design time.

```csharp
private ContactDialogRequest? _request;
private IContactDraftValidator? _validator;
private bool _runtimeConfigured;

[EditorBrowsable(EditorBrowsableState.Never)]
public ContactDialog()
{
    InitializeComponent();
}

public ContactDialog(
    ContactDialogRequest request,
    IContactDraftValidator validator)
    : this()
{
    ConfigureRuntime(request, validator);
}
```

`ConfigureRuntime` shall:

1. null-check and store the request/validator;
2. set Add/Edit title, form `AccessibleName`, OK text/accessibility text, and initial field values;
3. mark `_runtimeConfigured = true` only after static layout exists;
4. call `ValidateDraft()` once after initial values are assigned.

`Draft_TextChanged`, `OkButton_Click`, and `ContactDialog_Shown` must return without runtime work
when `_runtimeConfigured` is false. `ValidateDraft` may use guarded local non-null references or
throw only if called internally before configuration. The public parameterless constructor is
therefore safe for the designer and inert if accidentally used; it never acquires production or
fake dependencies and cannot produce a `ContactResult`.

Keep the existing dependency-taking constructor, result contract, exact field limits, validation
mapping, and internal test accessors unchanged.

### 3.2 New `ContactDialog.Designer.cs` — standard generated layout

Declare named fields for every selectable designer object:

```text
components
contactLayoutPanel
nameLabel
nameTextBox
numberLabel
numberTextBox
actionsFlowLayoutPanel
okButton
cancelButton
validationErrors
```

`InitializeComponent` contains only normal designer-serializable statements. It must not reference
`ContactDialogRequest`, `IContactDraftValidator`, `ContactDraftValidator`, or create runtime data.
Use named event handlers rather than lambdas.

Layout contract:

- `AutoScaleMode = Font` and designer-generated `AutoScaleDimensions`.
- `ClientSize = 520 x 190` logical design units.
- `MinimumSize = 540 x 230` outer-window logical units.
- fixed dialog, center parent, no maximize/minimize/taskbar.
- root `TableLayoutPanel`: three columns, rows for Name, Number, actions, outer padding 12.
- columns: label `AutoSize`, editor `Percent 100`, icon gutter `Absolute 32`.
- both text boxes `Dock=Fill`, `MaxLength=4096`.
- actions span all three columns, flow right-to-left, no wrapping.
- both buttons retain `AutoSize=true`, `GrowAndShrink`, minimum height 36, padding 12/6.
- the design-time/default OK button is disabled; runtime validation is the only code that enables it.
- `AcceptButton=okButton`; `CancelButton=cancelButton`; Cancel returns `DialogResult.Cancel`.

Create `validationErrors` with the component container, set `ContainerControl=this` and
`BlinkStyle=NeverBlink`, then explicitly apply to each text box:

```csharp
validationErrors.SetIconAlignment(textBox, ErrorIconAlignment.MiddleRight);
validationErrors.SetIconPadding(textBox, 4);
```

The 32-unit third column is reserved exclusively for the 16-unit system error icon, explicit
padding, and scaling/rounding tolerance. The text box must end before this column; do not solve
clipping with negative padding or left-aligned icons.

`ValidateDraft` continues calling `SetError`. It shall also set each text box
`AccessibleDescription` to its current error text (or empty when valid), so the same actionable
validation is available to accessibility clients as well as the ErrorProvider tooltip.

Add `ContactDialog.resx` as the standard minimal form resource. The designer owns future resource
changes.

### 3.3 `MainForm.cs` — designer construction without runtime behavior changes

Add a public parameterless UI-only constructor and chain the existing runtime constructor:

```csharp
[EditorBrowsable(EditorBrowsableState.Never)]
public MainForm()
{
    InitializeComponent();
}

public MainForm(bool isSafeDemo)
    : this()
{
    safeDemoBanner.Visible = isSafeDemo;
}
```

The default designer value of `safeDemoBanner.Visible` is `false`. `Program` and all existing
runtime/test callers continue using `MainForm(bool)`.

Do not compose a controller, settings, CSV, Twilio, or safe-demo fake from the parameterless
constructor. Existing event handlers already no-op when `_controller` is null, except closing;
change `MainForm_FormClosing` to return without cancellation when `_controller is null`. Real
runtime behavior is unchanged because `Program` attaches the controller before `Application.Run`.

Do not rely on `Control.DesignMode` inside either constructor; it is not a reliable constructor-time
signal. Configured-state/controller-null guards are the primary safety mechanism.
`LicenseManager.UsageMode` or `Site?.DesignMode` may be used only as a secondary guard around
strictly runtime-only callbacks if a concrete designer issue is reproduced.

### 3.4 `MainForm.Designer.cs` — make the existing layout round-trip

Preserve visual structure and behavior, but convert every local/anonymous component to a named
field so it appears in Document Outline and the designer can serialize edits:

```text
rootLayoutPanel
setupGroupBox
setupLayoutPanel
accountSidLabel
senderModeLabel
authTokenLabel
contactActionsFlowLayoutPanel
nameColumn
numberColumn
validationColumn
errorColumn
messageGroupBox
messageLayoutPanel
footerLayoutPanel
sendActionsFlowLayoutPanel
```

Keep all existing named controls and `selectedColumn`/`resultColumn`. Expand
`ConfigureButtonSizing` into explicit designer property assignments for all 12 buttons and remove
the helper call/method from the designer partial. Replace collection expressions, local variables,
and object-initializer-only controls with the ordinary code emitted by the current WinForms
Designer. Keep event hookup in `InitializeComponent`.

### 3.5 `RecipientDataGridView`

Move `RecipientDataGridView` unchanged from the nested `MainForm` type to:

```text
Forms/RecipientDataGridView.cs
```

It remains `internal sealed` in `HusayniaSMS.WinForms.Forms`, has an explicit parameterless
constructor if Visual Studio emits one, and retains all checkbox/highlight behavior. A top-level
type removes nested-type resolution as a designer risk without widening the public API.

If an actual supported Visual Studio version still refuses an internal custom control after this
move, the approved fallback is to make only this top-level type `public` and hide it from
IntelliSense; do not substitute a plain `DataGridView` at design time because that creates
different design/runtime control trees.

### 3.6 Project nesting metadata

Use standard filenames first. Add the following SDK `Update` metadata in
`HusayniaSMS.WinForms.csproj` to make form classification and nesting deterministic across Visual
Studio installations; do not use `Include`, which would duplicate SDK default items.

```xml
<ItemGroup>
  <Compile Update="Forms\ContactDialog.cs">
    <SubType>Form</SubType>
  </Compile>
  <Compile Update="Forms\ContactDialog.Designer.cs">
    <DependentUpon>ContactDialog.cs</DependentUpon>
  </Compile>
  <EmbeddedResource Update="Forms\ContactDialog.resx">
    <DependentUpon>ContactDialog.cs</DependentUpon>
  </EmbeddedResource>

  <Compile Update="Forms\MainForm.cs">
    <SubType>Form</SubType>
  </Compile>
  <Compile Update="Forms\MainForm.Designer.cs">
    <DependentUpon>MainForm.cs</DependentUpon>
  </Compile>
  <EmbeddedResource Update="Forms\MainForm.resx">
    <DependentUpon>MainForm.cs</DependentUpon>
  </EmbeddedResource>
</ItemGroup>
```

Do not add `AutoGen`, a resource code generator, a designer SDK package, or `.filenesting.json`.

## 4. Contracts and compatibility

- Existing runtime constructors remain source-compatible:
  `MainForm(bool)` and
  `ContactDialog(ContactDialogRequest, IContactDraftValidator)`.
- New public parameterless constructors are additive, UI-only, and hidden from ordinary
  IntelliSense with `EditorBrowsable(Never)`.
- `Program.cs`, `IUserDialogs`, controller contracts, contact schemas, CSV format, settings,
  Twilio composition, and send behavior do not change.
- Exact ErrorProvider messages, `ContactResult`, accessible names, button mnemonics, limits,
  accept/cancel semantics, and validation timing are preserved.
- No database, service, package, deployment, data migration, or real-send path is introduced.

## 5. Failure, concurrency, security, and observability

- Designer construction performs no I/O, network access, settings access, controller
  initialization, validation-service construction, or send composition.
- An unconfigured `ContactDialog` remains inert and cannot return a successful result.
- A controller-less `MainForm` may open/close for design/test purposes; all production actions
  remain unavailable because no controller is attached.
- UI state remains STA-bound. No new asynchronous work, retry, idempotency, or concurrency
  behavior is added.
- Validation values are contact data and must not be logged. No telemetry is added.
- The designer `.resx` files contain presentation resources only; do not serialize contacts,
  messages, settings, credentials, validators, or controller instances.

## 6. Verification design

Add tests without new packages.

### Automated STA tests

1. `ParameterlessFormsConstructAndDisposeOnSta`
   - `new MainForm()` and `new ContactDialog()` succeed on STA.
   - all named controls/components are non-null;
   - ContactDialog OK is disabled and `ContactResult` is null;
   - no controller, validator, transport, file, or network fake is created.
2. `RuntimeConstructorsRemainBehaviorCompatible`
   - retain all existing ContactDialog validation/result tests;
   - retain existing `MainForm(true)` safe-demo visibility and controller tests.
3. `ContactDialogReservesErrorIconBounds`
   - display an invalid dialog;
   - assert `MiddleRight` and padding 4 for both fields;
   - calculate `textBox.Right + iconPadding + validationErrors.Icon.Width` and assert it is within
     the root layout display rectangle/right gutter;
   - repeat for system font, 12pt, 16pt, and a 1.25 layout scale;
   - assert labels, text boxes, buttons, and icon allowance remain inside client bounds.
4. `ContactDialogValidationRemainsDiscoverable`
   - assert exact `GetError` strings and matching `AccessibilityObject.Description`;
   - correct the values and assert both clear.
5. `MainFormParameterlessInstanceCanClose`
   - show/close a controller-less form on STA and prove close is not canceled.
6. `RecipientGridIsTopLevelAndBehaviorIsUnchanged`
   - reflection asserts `DeclaringType == null`;
   - retain all existing native checkbox/highlight tests.

### Static project-shape tests

Create `Presentation/FormDesignerCompatibilityTests.cs`, reusing the existing solution-root search:

- assert both form `.cs`, `.Designer.cs`, and `.resx` files exist;
- parse the `.csproj` with `XDocument` and assert the exact `SubType`/`DependentUpon` `Update`
  entries above;
- assert each runtime form source and designer source declares the same partial type;
- assert each designer source contains `InitializeComponent` and no runtime dependency type names;
- parse both `.resx` files as XML;
- assert the custom grid source declares a top-level type and `MainForm.cs` no longer declares it.

Do not add Roslyn/MSBuild test dependencies for source-shape checks.

### Manual Visual Studio acceptance

On the supported Visual Studio with the .NET desktop workload:

1. confirm Solution Explorer nests both `.Designer.cs` and `.resx` files beneath each form;
2. open both forms with **View Designer** without exceptions;
3. select/move a harmless label, save, undo/revert, and rebuild to prove round-trip serialization;
4. run at Windows 125% scaling and with a large system font; trigger both required errors and
   confirm complete icons/tooltips, field text, labels, and buttons are visible;
5. run Add and Edit through the real controller/safe-demo UI and confirm no behavior or send-path
   change.

## 7. Tradeoffs and rejected alternatives

- **Chosen:** additive parameterless UI-only constructors. This is the smallest reliable designer
  seam and preserves runtime constructors.
- **Cost:** public construction surface grows. `EditorBrowsable`, inert unconfigured behavior, and
  unchanged production composition limit misuse.
- **Chosen:** named designer fields and serializer-style code. The diff is mechanical but future
  cosmetic edits become maintainable.
- **Rejected:** fake request/validator/controller objects in parameterless constructors. They blur
  production boundaries and may execute validation or startup behavior in the designer.
- **Rejected:** hand-maintaining all layout in runtime code. It preserves the current problem.
- **Rejected:** left-aligning error icons or using negative padding. It obscures the error/label
  relationship and does not establish a stable bounds invariant.
- **Rejected:** new custom designer/package or `.filenesting.json`. Standard WinForms partials and
  SDK item metadata are sufficient.
- **Rejected:** design-time substitution of the custom grid. Different control trees are harder to
  reason about and can hide runtime-only layout failures.

## 8. Risks and mitigation

- **Designer rewrites hand-authored code:** keep runtime logic out of `.Designer.cs`; accept the
  current designer's formatting and rerun build/tests after the first designer save.
- **Constructor-time design detection is false:** never depend on it; use configured-state/null
  guards.
- **Icon still clips under unusual metrics:** reserve a dedicated gutter and enforce the bounds
  invariant at multiple fonts/scales plus manual 125% QA.
- **Custom grid fails design-host resolution:** top-level internal type first; public top-level is
  the narrow fallback.
- **SDK duplicate item error:** use `Update`, never `Include`.
- **Accessibility regression:** preserve names/mnemonics and mirror current errors into
  `AccessibleDescription`.

## 9. Migration and rollback

This is a backward-compatible source refactor with no persisted-data migration. Implement one form
at a time while keeping the solution buildable: extract ContactDialog layout, add its tests, then
normalize MainForm and move the grid. Rollback is file-level: revert the partial/designer split and
metadata; no data or configuration rollback is required.

No CTO escalation is required: there is no breaking API/schema change, destructive migration,
external service, material cost, or accepted security/reliability tradeoff.
