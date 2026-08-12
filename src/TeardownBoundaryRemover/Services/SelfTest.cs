using System.Globalization;
using System.Text;

namespace TeardownBoundaryRemover.Services;

internal static class SelfTest
{
    public static int Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "TBR-SelfTest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var succeeded = false;
        try
        {
            TestBoundaryRemoval(root);
            TestNestedBoundary(root);
            TestCaseAndNamespaceAreIgnored(root);
            TestBoundaryNamedGroupIsPreserved(root);
            TestNamespacedSceneIsIgnored(root);
            TestNonTargetSkipsHash(root);
            TestParallelCandidateScan(root);
            TestMapSourceSelectionAndDeduplication(root);
            TestUtf16Encoding(root);
            TestDtdRejected(root);
            TestInfoTxt(root);
            TestRestoreRefusesExternalChanges(root);
            TestLocalizationRules();
            TestTdbinCopyOperation(root);
            TestBuiltInMapCatalog();
            succeeded = true;
            return 0;
        }
        catch (Exception ex)
        {
            try { File.WriteAllText(Path.Combine(root, "SELFTEST_FAILED.txt"), ex.ToString()); } catch { }
            return 2;
        }
        finally
        {
            // Keep a failed run's directory so the diagnostic written above is actually available.
            if (succeeded)
            {
                try { Directory.Delete(root, recursive: true); } catch { }
            }
        }
    }

    private static void TestBoundaryRemoval(string root)
    {
        var path = Path.Combine(root, "basic.xml");
        File.WriteAllText(path, "<?xml version=\"1.0\" encoding=\"utf-8\"?>\r\n<scene><environment name=\"保留\"/><boundary pos=\"0 0 0\"/><body name=\"also-keep\"/></scene>", new UTF8Encoding(false));
        var analysis = XmlBoundaryService.Analyze(path);
        Assert(analysis.IsLevelLike && analysis.BoundaryCount == 1 && analysis.Error is null, "basic analysis");
        Assert(XmlBoundaryService.RemoveBoundaryToOriginal(path, analysis.Sha256).RemovedCount == 1, "basic removal count");
        var text = File.ReadAllText(path);
        Assert(text.Contains("保留", StringComparison.Ordinal) && text.Contains("also-keep", StringComparison.Ordinal) && !text.Contains("<boundary", StringComparison.Ordinal), "basic preserved content");
    }

    private static void TestNestedBoundary(string root)
    {
        var path = Path.Combine(root, "nested.xml");
        File.WriteAllText(path, "<scene><group name=\"keep\"><boundary><location name=\"inside\"/></boundary><location name=\"outside\"/></group></scene>");
        var analysis = XmlBoundaryService.Analyze(path);
        XmlBoundaryService.RemoveBoundaryToOriginal(path, analysis.Sha256);
        var text = File.ReadAllText(path);
        Assert(!text.Contains("inside", StringComparison.Ordinal) && text.Contains("outside", StringComparison.Ordinal), "nested boundary removal");
    }

    private static void TestCaseAndNamespaceAreIgnored(string root)
    {
        var casePath = Path.Combine(root, "case.xml");
        File.WriteAllText(casePath, "<scene><Boundary/><boundary/><body/></scene>");
        var analysis = XmlBoundaryService.Analyze(casePath);
        Assert(analysis.BoundaryCount == 1, "upper-case Boundary must be ignored");
        XmlBoundaryService.RemoveBoundaryToOriginal(casePath, analysis.Sha256);
        Assert(File.ReadAllText(casePath).Contains("<Boundary", StringComparison.Ordinal), "upper-case element preserved");

        var nsPath = Path.Combine(root, "namespace.xml");
        File.WriteAllText(nsPath, "<scene xmlns:x=\"urn:test\"><x:boundary/><boundary/></scene>");
        analysis = XmlBoundaryService.Analyze(nsPath);
        Assert(analysis.BoundaryCount == 1, "namespaced boundary must be ignored");
        XmlBoundaryService.RemoveBoundaryToOriginal(nsPath, analysis.Sha256);
        Assert(File.ReadAllText(nsPath).Contains("x:boundary", StringComparison.Ordinal), "namespaced element preserved");
    }

    private static void TestBoundaryNamedGroupIsPreserved(string root)
    {
        var path = Path.Combine(root, "group.xml");
        File.WriteAllText(path, "<scene><group name=\"Boundary\"><body name=\"ordinary-content\"/></group></scene>");
        var analysis = XmlBoundaryService.Analyze(path);
        Assert(analysis.BoundaryCount == 0 && analysis.IgnoredBoundaryGroupCount == 1 && analysis.Warning is not null, "Boundary group must be informational only");
        Assert(File.ReadAllText(path).Contains("ordinary-content", StringComparison.Ordinal), "Boundary group untouched");
    }

    private static void TestNamespacedSceneIsIgnored(string root)
    {
        var path = Path.Combine(root, "scene-namespace.xml");
        File.WriteAllText(path, "<scene xmlns=\"urn:not-teardown\"><boundary/></scene>");
        var analysis = XmlBoundaryService.Analyze(path);
        Assert(!analysis.IsLevelLike && analysis.BoundaryCount == 0, "namespaced scene must never be editable");
    }

    private static void TestNonTargetSkipsHash(string root)
    {
        var path = Path.Combine(root, "no-target.xml");
        File.WriteAllText(path, "<scene><body name=\"keep\"/></scene>");
        var analysis = XmlBoundaryService.Analyze(path);
        Assert(analysis.IsLevelLike && analysis.BoundaryCount == 0 && analysis.Sha256.Length == 0, "non-target scan must skip the second full-file hash read");
    }

    private static void TestParallelCandidateScan(string root)
    {
        var modRoot = Path.Combine(root, "isolated-documents", "Teardown", "mods", "parallel-scan");
        Directory.CreateDirectory(modRoot);
        File.WriteAllText(Path.Combine(modRoot, "info.txt"), "name = Parallel Scan\n");
        for (var i = 0; i < 12; i++)
            File.WriteAllText(Path.Combine(modRoot, $"scene-{i:D2}.xml"), i % 2 == 0 ? "<scene><boundary/></scene>" : "<scene><body/></scene>");

        var scanner = new TeardownScanner(() => new SteamDiscovery(), () => Path.Combine(root, "isolated-documents"));
        var report = scanner.ScanAll([], new ScanOptions(false, false, true));
        var item = report.Items.Single(x => x.RootPath.Equals(modRoot, StringComparison.OrdinalIgnoreCase));
        Assert(item.XmlFiles.Count == 6 && item.BoundaryCount == 6 && item.XmlFiles.SequenceEqual(item.XmlFiles.OrderBy(x => x.RelativePath, StringComparer.OrdinalIgnoreCase)), "parallel boundary candidate scan is complete and deterministic");
    }

    private static void TestMapSourceSelectionAndDeduplication(string root)
    {
        var install = Path.Combine(root, "fake-game");
        var bin = Path.Combine(install, "data", "bin");
        var builtInMod = Path.Combine(install, "mods", "official-example");
        var workshop = Path.Combine(root, "fake-workshop");
        var workshopMap = Path.Combine(workshop, "123456");
        Directory.CreateDirectory(bin);
        Directory.CreateDirectory(builtInMod);
        Directory.CreateDirectory(workshopMap);
        File.WriteAllBytes(Path.Combine(bin, "lee_sandbox.bin"), [0x78, 0x9C, 0x00]);
        File.WriteAllText(Path.Combine(builtInMod, "info.txt"), "name = Official Example\n");
        File.WriteAllText(Path.Combine(builtInMod, "main.xml"), "<scene><boundary/></scene>");
        File.WriteAllText(Path.Combine(workshopMap, "info.txt"), "name = Workshop Example\n");
        File.WriteAllText(Path.Combine(workshopMap, "main.xml"), "<scene><boundary/></scene>");

        var discovery = new SteamDiscovery { TeardownInstallPath = install };
        discovery.WorkshopRoots.Add(workshop);
        discovery.WorkshopRoots.Add(workshop); // duplicate discovery input must not duplicate results
        var scanner = new TeardownScanner(() => discovery);
        var report = scanner.ScanAll([], new ScanOptions(true, true, false));

        Assert(report.Items.Count(x => x.IsCompiledBinaryMap && x.Name.Contains("Lee Chemicals", StringComparison.Ordinal) &&
            x.BuiltInKind == BuiltInContentKind.Sandbox && x.OriginalFileName == "lee_sandbox.bin") == 1,
            "compiled built-in sandbox identified and listed once");
        Assert(report.Items.Count(x => x.SourceType == ContentSourceType.BuiltIn && x.Name == "Official Example") == 1, "built-in XML map listed once");
        Assert(report.Items.Count(x => x.SourceType == ContentSourceType.Workshop && x.Name == "Workshop Example") == 1, "Workshop map listed once");
        Assert(report.Items.Where(x => x.Name is "Official Example" or "Workshop Example").All(x => x.IsSelectable), "eligible XML maps remain selectable");

        var builtInOnly = scanner.ScanAll([], new ScanOptions(true, false, false));
        Assert(builtInOnly.Items.All(x => x.SourceType == ContentSourceType.BuiltIn), "source checkboxes limit scanning");
    }

    private static void TestUtf16Encoding(string root)
    {
        var path = Path.Combine(root, "utf16.xml");
        File.WriteAllText(path, "<?xml version=\"1.0\" encoding=\"utf-16\"?><scene><boundary/><body name=\"中文\"/></scene>", new UnicodeEncoding(false, true, true));
        var analysis = XmlBoundaryService.Analyze(path);
        XmlBoundaryService.RemoveBoundaryToOriginal(path, analysis.Sha256);
        var bytes = File.ReadAllBytes(path);
        Assert(bytes.Length > 2 && bytes[0] == 0xFF && bytes[1] == 0xFE && File.ReadAllText(path, Encoding.Unicode).Contains("中文", StringComparison.Ordinal), "UTF-16 BOM and content preserved");
    }

    private static void TestDtdRejected(string root)
    {
        var path = Path.Combine(root, "dtd.xml");
        File.WriteAllText(path, "<!DOCTYPE scene [<!ENTITY x SYSTEM \"file:///C:/Windows/win.ini\">]><scene><body name=\"&x;\"/></scene>");
        Assert(XmlBoundaryService.Analyze(path).Error is not null, "DTD must be rejected");
    }

    private static void TestInfoTxt(string root)
    {
        var modRoot = Path.Combine(root, "mod"); Directory.CreateDirectory(modRoot);
        File.WriteAllText(Path.Combine(modRoot, "info.txt"), "name = Example Map\nauthor = Example Author\n");
        var info = InfoTxtParser.Read(modRoot);
        Assert(info.Name == "Example Map" && info.Author == "Example Author", "info.txt parsing");

        var localizedRoot = Path.Combine(root, "localized-mod"); Directory.CreateDirectory(localizedRoot);
        File.WriteAllText(Path.Combine(localizedRoot, "info.txt"), "en_name = English Map\nsc_name = 中文地图\nauthor = Localized Author\n");
        info = InfoTxtParser.Read(localizedRoot);
        Assert(info.Name == (Loc.IsChinese ? "中文地图" : "English Map") && info.Author == "Localized Author", "localized info.txt name parsing");

        var englishOnlyRoot = Path.Combine(root, "english-only-mod"); Directory.CreateDirectory(englishOnlyRoot);
        File.WriteAllText(Path.Combine(englishOnlyRoot, "info.txt"), "en_name = Workshop Display Name\nauthor = Author\n");
        Assert(InfoTxtParser.Read(englishOnlyRoot).Name == "Workshop Display Name", "en_name fallback parsing");
    }

    private static void TestRestoreRefusesExternalChanges(string root)
    {
        var original = Path.Combine(root, "restore.xml");
        var backup = Path.Combine(root, "restore.backup.xml");
        File.WriteAllText(original, "<scene><boundary/></scene>"); File.Copy(original, backup);
        var originalHash = HashUtil.Sha256File(original);
        var session = new BackupSession { DirectoryPath = root, ManifestPath = Path.Combine(root, "restore-manifest.json"), Manifest = new BackupManifest { Entries = [new BackupEntry { OriginalPath = original, BackupPath = backup, OriginalSha256 = originalHash, ModifiedSha256 = "DEADBEEF" }] } };
        File.WriteAllText(original, "<scene><body name=\"external\"/></scene>");
        var threw = false; try { BackupManager.Restore(session); } catch (IOException) { threw = true; }
        Assert(threw && File.ReadAllText(original).Contains("external", StringComparison.Ordinal), "restore must not overwrite external changes");

        // A verified tool-written version is restored atomically and returns to the original hash.
        File.WriteAllText(original, "<scene><body name=\"modified-by-tool\"/></scene>");
        session.Manifest.Entries[0].ModifiedSha256 = HashUtil.Sha256File(original);
        BackupManager.Restore(session);
        Assert(HashUtil.Sha256File(original).Equals(originalHash, StringComparison.OrdinalIgnoreCase), "restore verified tool version");
    }

    private static void TestLocalizationRules()
    {
        Assert(Loc.IsChineseCulture(CultureInfo.GetCultureInfo("zh-CN")), "zh-CN selects Chinese");
        Assert(Loc.IsChineseCulture(CultureInfo.GetCultureInfo("zh-TW")), "zh-TW selects Chinese");
        Assert(!Loc.IsChineseCulture(CultureInfo.GetCultureInfo("en-US")), "en-US selects English");
        Assert(!Loc.IsChineseCulture(CultureInfo.GetCultureInfo("ja-JP")), "non-Chinese selects English");
    }

    private static void TestTdbinCopyOperation(string root)
    {
        var source = Environment.GetEnvironmentVariable("TBR_TEST_TDBIN_PATH");
        if (string.IsNullOrWhiteSpace(source) || !File.Exists(source))
            return;

        var copy = Path.Combine(root, "lee_sandbox-copy.bin");
        File.Copy(source, copy);
        var analysis = TdbinBoundaryService.Analyze(copy);
        Assert(analysis.IsSupported, "TDBIN 2.0.4 copy analysis");
        Assert(analysis.VertexCount is 0 or 31, "known Lee sandbox boundary count");
        if (analysis.VertexCount == 0) return;
        var sourceHash = HashUtil.Sha256File(source);
        var item = new ModItem
        {
            Name = "Lee sandbox copy",
            RootPath = root,
            SourceType = ContentSourceType.BuiltIn,
            IsCompiledBinaryMap = true,
            BinaryPath = copy,
            BinarySha256 = analysis.Sha256,
            BinaryVersion = analysis.Version,
            BinaryBoundaryVertexCount = analysis.VertexCount
        };
        var result = TdbinBoundaryService.BackupAndRemove(item, Path.Combine(root, "bin-backups"));
        Assert(result.RemovedVertices == 31 && TdbinBoundaryService.Analyze(copy).VertexCount == 0, "TDBIN copy boundary removal");
        Assert(File.Exists(result.BackupPath) && HashUtil.Sha256File(result.BackupPath) == analysis.Sha256, "TDBIN verified copy backup");
        Assert(HashUtil.Sha256File(source) == sourceHash, "installed TDBIN remains unchanged during copy test");
    }

    private static void TestBuiltInMapCatalog()
    {
        var leeSandbox = BuiltInMapCatalog.Identify("C:\\game\\data\\bin\\lee_sandbox.bin");
        Assert(leeSandbox.SourceDetail.Contains(Loc.T("沙盒", "Sandbox"), StringComparison.Ordinal) && leeSandbox.DisplayName.Contains("Lee Chemicals", StringComparison.Ordinal), "catalog sandbox classification");
        var leeMission = BuiltInMapCatalog.Identify("C:\\game\\data\\bin\\lee_computers.bin");
        Assert(leeMission.SourceDetail.Contains(Loc.T("任务", "Mission"), StringComparison.Ordinal) && leeMission.OriginalFileName == "lee_computers.bin", "catalog mission classification");
        Assert(BuiltInMapCatalog.Identify("C:\\game\\data\\bin\\ch_lee_fetch.bin").Category == Loc.T("挑战模式", "Challenge mode"), "catalog challenge classification");
        Assert(BuiltInMapCatalog.Identify("C:\\game\\data\\bin\\hub10.bin").Category.Contains(Loc.T("战役中枢", "Campaign hub"), StringComparison.Ordinal), "catalog hub classification");
        Assert(BuiltInMapCatalog.Identify("C:\\game\\data\\bin\\ending20.bin").Category.Contains(Loc.T("战役结局", "Campaign ending"), StringComparison.Ordinal), "catalog ending classification");
        Assert(BuiltInMapCatalog.Identify("C:\\game\\data\\bin\\menu.bin").Category == Loc.T("系统界面", "System UI"), "catalog system classification");
    }

    private static void Assert(bool condition, string name) { if (!condition) throw new InvalidOperationException("Self-test failed: " + name); }
}
