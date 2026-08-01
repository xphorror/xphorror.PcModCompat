using Xphorror.PcModCompat;

if (args.Length < 2)
{
    Console.Error.WriteLine("Usage: UiRecipeTool validate <ui_recipe.bin>");
    Console.Error.WriteLine("       UiRecipeTool emit <mod-folder> <output-file> [game-revision]");
    Console.Error.WriteLine("       UiRecipeTool fixture <output-file>");
    return 2;
}

if (args[0].Equals("validate", StringComparison.OrdinalIgnoreCase))
{
    var path = Path.GetFullPath(args[1]);
    if (!PcCompatUiRecipeBinary.TryValidate(path, out var error))
    {
        Console.Error.WriteLine($"invalid path={path} error={error}");
        return 1;
    }

    Console.WriteLine($"valid path={path} size={new FileInfo(path).Length} schema={PcCompatUiRecipeBinary.SchemaVersion}");
    return 0;
}

if (args[0].Equals("fixture", StringComparison.OrdinalIgnoreCase))
{
    var fixtureOutputPath = Path.GetFullPath(args[1]);
    var fixtureManifest = new PcModManifest
    {
        FolderPath = Path.GetDirectoryName(fixtureOutputPath)!,
        Id = "UiRecipeVmFixture",
        DisplayName = "UI Recipe VM Fixture",
        Kind = PcModKind.UnityModManager
    };
    var fixtureReport = new PcCompatRecipeCompileReport
    {
        ModId = fixtureManifest.Id,
        RecipeId = "xphorror.fixture.ui_recipe_vm.v1",
        Compatibility = "test",
        Rules = new[]
        {
            new PcCompatCompiledRule
            {
                Id = "fixture.rule",
                FeatureId = "fixture",
                TargetType = "FixtureTarget",
                TargetMethod = "Tick",
                ParamCount = 0,
                TargetIsStatic = false,
                TargetReturnType = "System.Void",
                TargetParameterTypes = Array.Empty<string>(),
                Stage = PcCompatRuleStage.AfterOriginal,
                Op = PcCompatRuleOp.OverlayShow,
                RequiredCapabilities = PcCompatCapability.UiOverlay
            }
        },
        RequiredCapabilities = PcCompatCapability.UiOverlay,
        UiObjectGraph = new[]
        {
            new PcCompatUiObjectNode
            {
                Id = 9,
                Name = "Fixture.Canvas",
                Components = PcCompatUiComponentMask.RectTransform |
                             PcCompatUiComponentMask.Canvas |
                             PcCompatUiComponentMask.CanvasScaler,
                Flags = PcCompatUiObjectFlags.ActiveInitially |
                        PcCompatUiObjectFlags.DontDestroyOnLoad,
                Initialization = new[]
                {
                    new PcCompatUiComponentOperation
                    {
                        OpCode = PcCompatUiComponentOpCode.SetCanvasRenderMode,
                        Payload0 = 0
                    },
                    new PcCompatUiComponentOperation
                    {
                        OpCode = PcCompatUiComponentOpCode.SetCanvasReferenceResolution,
                        Payload0 = BitConverter.SingleToInt32Bits(1920f),
                        Payload1 = BitConverter.SingleToInt32Bits(1080f)
                    }
                }
            },
            new PcCompatUiObjectNode
            {
                Id = 10,
                ParentId = 9,
                Name = "Fixture.Text",
                Components = PcCompatUiComponentMask.RectTransform |
                             PcCompatUiComponentMask.TextMeshProUGUI |
                             PcCompatUiComponentMask.CanvasRenderer |
                             PcCompatUiComponentMask.ContentSizeFitter,
                Flags = PcCompatUiObjectFlags.ActiveInitially,
                Initialization = new[]
                {
                    new PcCompatUiComponentOperation
                    {
                        OpCode = PcCompatUiComponentOpCode.SetText,
                        StringValue = "UI recipe VM fixture"
                    },
                    new PcCompatUiComponentOperation
                    {
                        OpCode = PcCompatUiComponentOpCode.SetTextFontSize,
                        Payload0 = BitConverter.SingleToInt32Bits(24f)
                    },
                    new PcCompatUiComponentOperation
                    {
                        OpCode = PcCompatUiComponentOpCode.SetTextLineSpacing,
                        Payload0 = BitConverter.SingleToInt32Bits(30f)
                    },
                    new PcCompatUiComponentOperation
                    {
                        OpCode = PcCompatUiComponentOpCode.SetContentSizeHorizontalFit,
                        Payload0 = 2
                    },
                    new PcCompatUiComponentOperation
                    {
                        OpCode = PcCompatUiComponentOpCode.SetContentSizeVerticalFit,
                        Payload0 = 2
                    }
                }
            }
        },
        UiLifecyclePrograms = new[]
        {
            new PcCompatUiLifecycleProgram
            {
                Id = "fixture.lifecycle",
                RuntimeRuleId = 1001,
                Trigger = PcCompatUiLifecycleTrigger.BundleLoad,
                ClockDomain = PcCompatUiClockDomain.Realtime,
                Flags = PcCompatUiLifecycleFlags.RequireInputSnapshot,
                InstructionBudget = 64,
                CommandType = (uint)PcCompatPresentationCommandType.EnsureGraph,
                TargetId = 9,
                InitialDelayNs = 1_000_000,
                DeferredRetryDelayNs = 5_000_000,
                Instructions = new[]
                {
                    new PcCompatNativeVmInstruction(PcCompatNativeVmOpcode.LoadConstI64, Destination: 0, Payload: 42),
                    new PcCompatNativeVmInstruction(PcCompatNativeVmOpcode.LoadConstF64, Destination: 0, Payload: BitConverter.DoubleToInt64Bits(1.5)),
                    new PcCompatNativeVmInstruction(PcCompatNativeVmOpcode.Return)
                }
            }
        }
    };

    Directory.CreateDirectory(Path.GetDirectoryName(fixtureOutputPath)!);
    PcCompatUiRecipeBinary.Write(fixtureOutputPath, fixtureManifest, fixtureReport, 143);
    if (!PcCompatUiRecipeBinary.TryValidate(fixtureOutputPath, out var error))
    {
        Console.Error.WriteLine($"fixture validation failed path={fixtureOutputPath} error={error}");
        return 1;
    }
    Console.WriteLine($"fixture path={fixtureOutputPath} size={new FileInfo(fixtureOutputPath).Length}");
    return 0;
}

if (!args[0].Equals("emit", StringComparison.OrdinalIgnoreCase) || args.Length < 3)
{
    Console.Error.WriteLine("Unknown command or missing arguments.");
    return 2;
}

var modFolder = Path.GetFullPath(args[1]);
var outputPath = Path.GetFullPath(args[2]);
var gameRevision = args.Length >= 4 && int.TryParse(args[3], out var parsedRevision)
    ? parsedRevision
    : PcCompatStaticPatchScanner.DefaultTargetGameRevision;

if (!PcModManifestReader.TryRead(modFolder, out var manifest, out var manifestError))
{
    Console.Error.WriteLine($"manifest failed path={modFolder} error={manifestError}");
    return 1;
}

var scan = PcCompatStaticPatchScanner.Scan(manifest, gameRevision);
var translation = PcCompatCallbackTranslator.Translate(manifest, scan);
if (!PcCompatRecipeCompiler.TryCompile(manifest, scan, translation, out var report, out var recipeError))
{
    Console.Error.WriteLine($"recipe failed mod={manifest.Id} error={recipeError}");
    return 1;
}

Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
PcCompatUiRecipeBinary.Write(outputPath, manifest, report, gameRevision);
if (!PcCompatUiRecipeBinary.TryValidate(outputPath, out var validationError))
{
    Console.Error.WriteLine($"emitted recipe failed validation path={outputPath} error={validationError}");
    return 1;
}

Console.WriteLine(
    $"emitted path={outputPath} mod={report.ModId} targets={PcCompatRuntimeRuleBundle.FromReport(report).Targets.Count} " +
    $"rules={report.Rules.Count} size={new FileInfo(outputPath).Length} revision={gameRevision}");
return 0;
