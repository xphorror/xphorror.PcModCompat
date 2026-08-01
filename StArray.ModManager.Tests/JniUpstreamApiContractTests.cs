using System.Text.RegularExpressions;

namespace StArray.ModManager.Tests;

public sealed class JniUpstreamApiContractTests
{
    private static readonly string[] ExpectedUpstreamEntries =
    [
        "jnihelper_new_global_ref",
        "jnihelper_new_local_ref",
        "jnihelper_delete_local_ref",
        "jnihelper_delete_global_ref",
        "jnihelper_get_object_class",
        "jnihelper_check_exception",
        "jnihelper_clear_exception",
        "jnihelper_find_class",
        "jnihelper_get_method_id",
        "jnihelper_get_static_method_id",
        "jnihelper_get_field_id",
        "jnihelper_get_static_field_id",
        "jnihelper_new_string",
        "jnihelper_new_string_utf",
        "jnihelper_get_string_utf_chars",
        "jnihelper_release_string_utf_chars",
        "jnihelper_get_string_length",
        "jnihelper_call_object_method_a",
        "jnihelper_call_boolean_method_a",
        "jnihelper_call_byte_method_a",
        "jnihelper_call_char_method_a",
        "jnihelper_call_short_method_a",
        "jnihelper_call_int_method_a",
        "jnihelper_call_long_method_a",
        "jnihelper_call_float_method_a",
        "jnihelper_call_double_method_a",
        "jnihelper_call_void_method_a",
        "jnihelper_call_object_method",
        "jnihelper_call_static_object_method_a",
        "jnihelper_call_static_boolean_method_a",
        "jnihelper_call_static_byte_method_a",
        "jnihelper_call_static_char_method_a",
        "jnihelper_call_static_short_method_a",
        "jnihelper_call_static_int_method_a",
        "jnihelper_call_static_long_method_a",
        "jnihelper_call_static_float_method_a",
        "jnihelper_call_static_double_method_a",
        "jnihelper_call_static_void_method_a",
        "jnihelper_call_static_object_method",
        "jnihelper_get_object_field",
        "jnihelper_get_boolean_field",
        "jnihelper_get_byte_field",
        "jnihelper_get_char_field",
        "jnihelper_get_short_field",
        "jnihelper_get_int_field",
        "jnihelper_get_long_field",
        "jnihelper_get_float_field",
        "jnihelper_get_double_field",
        "jnihelper_set_object_field",
        "jnihelper_set_boolean_field",
        "jnihelper_set_byte_field",
        "jnihelper_set_char_field",
        "jnihelper_set_short_field",
        "jnihelper_set_int_field",
        "jnihelper_set_long_field",
        "jnihelper_set_float_field",
        "jnihelper_set_double_field",
        "jnihelper_get_static_object_field",
        "jnihelper_get_static_boolean_field",
        "jnihelper_get_static_byte_field",
        "jnihelper_get_static_char_field",
        "jnihelper_get_static_short_field",
        "jnihelper_get_static_int_field",
        "jnihelper_get_static_long_field",
        "jnihelper_get_static_float_field",
        "jnihelper_get_static_double_field",
        "jnihelper_set_static_object_field",
        "jnihelper_set_static_boolean_field",
        "jnihelper_set_static_byte_field",
        "jnihelper_set_static_char_field",
        "jnihelper_set_static_short_field",
        "jnihelper_set_static_int_field",
        "jnihelper_set_static_long_field",
        "jnihelper_set_static_float_field",
        "jnihelper_set_static_double_field",
        "jnihelper_get_array_length",
        "jnihelper_get_object_array_element",
        "jnihelper_set_object_array_element",
        "jnihelper_get_current_activity",
        "jnihelper_get_unity_surface",
        "jnihelper_surface_to_native_window",
        "jnihelper_keyevent_get_unicode",
        "jnihelper_set_data",
        "jnihelper_get_data_len",
        "jnihelper_get_data_buf"
    ];

    [Test]
    public void AndroidJniSurfaceContainsAllUpstreamEntriesOnBothSides()
    {
        var root = FindRepoRoot();
        var managed = File.ReadAllText(Path.Combine(
            root, "StArray.ModManager.Android", "Native", "JniHelperNative.cs"));
        var native = File.ReadAllText(Path.Combine(
            root, "Android", "library", "src", "main", "cpp", "core", "jni_helper.c"));
        var buildScript = File.ReadAllText(Path.Combine(root, "build.ps1"));
        var managedEntries = Regex.Matches(
                managed,
                "EntryPoint\\s*=\\s*\\\"(jnihelper_[a-z0-9_]+)\\\"")
            .Select(match => match.Groups[1].Value)
            .ToHashSet(StringComparer.Ordinal);
        var nativeEntries = Regex.Matches(
                native,
                "(?m)^\\s*[A-Za-z_][A-Za-z0-9_\\s*]*\\s+(jnihelper_[a-z0-9_]+)\\s*\\([^;]*\\)\\s*\\{")
            .Select(match => match.Groups[1].Value)
            .ToHashSet(StringComparer.Ordinal);

        Assert.Multiple(() =>
        {
            Assert.That(ExpectedUpstreamEntries, Has.Length.EqualTo(85));
            Assert.That(
                ExpectedUpstreamEntries.Distinct(StringComparer.Ordinal).ToArray(),
                Has.Length.EqualTo(85));
            Assert.That(
                ExpectedUpstreamEntries.Where(entry => !managedEntries.Contains(entry)),
                Is.Empty,
                "managed JNI bindings are incomplete");
            Assert.That(
                ExpectedUpstreamEntries.Where(entry => !nativeEntries.Contains(entry)),
                Is.Empty,
                "native JNI exports are incomplete");
            Assert.That(buildScript, Does.Contain("llvm-readelf JNI export audit failed"));
            Assert.That(buildScript, Does.Contain("JNI helper exports missing from final SO"));
        });
    }

    [Test]
    public void AndroidJniPrimitiveMarshallingMatchesJniWidths()
    {
        var root = FindRepoRoot();
        var managed = File.ReadAllText(Path.Combine(
            root, "StArray.ModManager.Android", "Native", "JniHelperNative.cs"));
        var jvalue = File.ReadAllText(Path.Combine(
            root, "StArray.ModManager.Android", "Native", "JValue.cs"));

        Assert.Multiple(() =>
        {
            foreach (var method in new[]
                     {
                         "CheckException",
                         "CallBooleanMethodA",
                         "CallStaticBooleanMethodA",
                         "GetBooleanField",
                         "GetStaticBooleanField"
                     })
            {
                Assert.That(MethodDeclaration(managed, method), Does.Contain(
                    "[return: MarshalAs(UnmanagedType.I1)]"), method);
            }

            foreach (var method in new[] { "SetBooleanField", "SetStaticBooleanField" })
            {
                Assert.That(MethodDeclaration(managed, method), Does.Contain(
                    "[MarshalAs(UnmanagedType.I1)] bool value"), method);
            }

            foreach (var method in new[]
                     {
                         "CallCharMethodA",
                         "CallStaticCharMethodA",
                         "GetCharField",
                         "GetStaticCharField"
                     })
            {
                Assert.That(MethodDeclaration(managed, method), Does.Contain(
                    "[return: MarshalAs(UnmanagedType.U2)]"), method);
            }

            foreach (var method in new[] { "SetCharField", "SetStaticCharField" })
            {
                Assert.That(MethodDeclaration(managed, method), Does.Contain(
                    "[MarshalAs(UnmanagedType.U2)] char value"), method);
            }

            Assert.That(MethodDeclaration(managed, "NewStringUtf"), Does.Contain(
                "[MarshalAs(UnmanagedType.LPUTF8Str)] string utf"));
            Assert.That(jvalue, Does.Contain("StructLayout(LayoutKind.Explicit, Size = 8)"));
            Assert.That(jvalue, Does.Contain("[FieldOffset(0)] public byte Z"));
            Assert.That(jvalue, Does.Contain("[FieldOffset(0)] public char C"));
        });
    }

    private static string MethodDeclaration(string source, string methodName)
    {
        var method = source.IndexOf($" {methodName}(", StringComparison.Ordinal);
        Assert.That(method, Is.GreaterThanOrEqualTo(0), $"method {methodName} is missing");
        var start = source.LastIndexOf("[DllImport", method, StringComparison.Ordinal);
        var end = source.IndexOf(';', method);
        Assert.That(start, Is.GreaterThanOrEqualTo(0), $"DllImport for {methodName} is missing");
        Assert.That(end, Is.GreaterThan(method), $"declaration for {methodName} is incomplete");
        return source[start..(end + 1)];
    }

    private static string FindRepoRoot()
    {
        var current = new DirectoryInfo(TestContext.CurrentContext.WorkDirectory);
        while (current != null)
        {
            if (Directory.Exists(Path.Combine(current.FullName, "StArray.ModManager.Android")) &&
                Directory.Exists(Path.Combine(current.FullName, "Android")))
            {
                return current.FullName;
            }
            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate StArray.ModManager repo root");
    }
}
