using System.ComponentModel;
using System.Text;
using System.Text.Json;
using LineRename.Core;

namespace LineRename.Tests;

internal static class Program
{
    private static readonly List<(string Name, Action<Sandbox> Body)> Tests = [];

    private static int Main()
    {
        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine("These tests require Windows and real Windows file identities.");
            return 1;
        }

        RegisterTests();
        var failures = new List<string>();
        foreach (var (name, body) in Tests)
        {
            try
            {
                using var sandbox = new Sandbox();
                body(sandbox);
                Console.WriteLine($"PASS {name}");
            }
            catch (Exception error)
            {
                failures.Add(name);
                Console.Error.WriteLine($"FAIL {name}\n{error}");
            }
        }

        Console.WriteLine($"\nTotal: {Tests.Count}; Passed: {Tests.Count - failures.Count}; Failed: {failures.Count}");
        foreach (var failure in failures) Console.Error.WriteLine($"  FAILED: {failure}");
        return failures.Count == 0 ? 0 : 1;
    }

    private static void Add(string name, Action<Sandbox> body) => Tests.Add((name, body));

    private static void RegisterTests()
    {
        Add("Parse CRLF, CR, LF, trimming and blank lines", _ =>
            SequenceEqual(["one", "two", "three", "four"],
                RenamePlanner.ParseNames(" \tone \t\r\n\r\ntwo\rthree\n\t\nfour\r")));
        Add("Parse Unicode without normalization or losing interior spaces", _ =>
            SequenceEqual(["猫", "résumé", "e\u0301", "two  words", "Ω"],
                RenamePlanner.ParseNames("\u2003猫\u2003\r\nrésumé\re\u0301\ntwo  words\nΩ")));
        Add("Parse empty input", _ => SequenceEqual(Array.Empty<string>(), RenamePlanner.ParseNames("")));
        Add("Parse whitespace-only input", _ =>
            SequenceEqual(Array.Empty<string>(), RenamePlanner.ParseNames(" \r\n\t\r\u2003\n")));

        Add("Too few names reports missing row and count error", s =>
        {
            var paths = new[] { s.File("a.txt", "a"), s.File("b.txt", "b") };
            var plan = RenamePlanner.Create(paths, ["new"], true);
            Invalid(plan);
            Equal(2, plan.Rows.Count);
            Check(plan.Rows[1].Error is not null, "Missing name must mark its row.");
            Check(plan.Errors.Count >= 2, "Expected count and row errors.");
            s.AssertFiles(("a.txt", "a"), ("b.txt", "b"));
        });
        Add("Too many names blocks execution", s =>
            Invalid(RenamePlanner.Create([s.File("a", "a")], ["b", "c"], false)));
        Add("Names without sources blocks execution", _ =>
            Invalid(RenamePlanner.Create([], ["a"], false)));
        Add("Empty plan cannot execute", s =>
        {
            var plan = RenamePlanner.Create([], [], true);
            Check(!plan.CanExecute, "Empty plan must not execute.");
            Equal(0, plan.Rows.Count);
            Throws<InvalidOperationException>(() => s.Executor().Execute([]));
            s.AssertNoJournals();
        });
        Add("Preserve only the final extension, retaining its case", s =>
        {
            var path = s.File("archive.tar.GZ", "archive");
            var plan = RenamePlanner.Create([path], ["renamed.v2"], true);
            Valid(plan);
            Equal(s.PathOf("renamed.v2.GZ"), plan.Entries[0].TargetPath);
            Equal("archive.tar.GZ", plan.Entries[0].SourceName);
            Equal("renamed.v2.GZ", plan.Entries[0].TargetName);
        });
        Add("Full-name mode replaces the old extension", s =>
        {
            var plan = RenamePlanner.Create([s.File("a.txt", "a")], ["b.csv"], false);
            Valid(plan);
            Equal(s.PathOf("b.csv"), plan.Entries[0].TargetPath);
        });
        Add("Full-name mode can remove the extension", s =>
        {
            var plan = RenamePlanner.Create([s.File("a.txt", "a")], ["b"], false);
            Valid(plan);
            Equal(s.PathOf("b"), plan.Entries[0].TargetPath);
        });
        Add("No extension does not add a dot", s =>
        {
            var plan = RenamePlanner.Create([s.File("LICENSE", "license")], ["NOTICE"], true);
            Valid(plan);
            Equal(s.PathOf("NOTICE"), plan.Entries[0].TargetPath);
        });
        Add("Unicode preview preserves requested spelling", s =>
        {
            var plan = RenamePlanner.Create([s.File("源.txt", "源")], ["résumé-e\u0301-猫"], true);
            Valid(plan);
            Equal("résumé-e\u0301-猫.txt", plan.Entries[0].TargetName);
        });

        var invalidNames = new[] { "", " ", ".", "..", "trailing.", "trailing ", new string('x', 256) };
        for (var i = 0; i < invalidNames.Length; i++)
        {
            var name = invalidNames[i];
            Add($"Reject invalid name case {i + 1}", s => InvalidName(s, name));
        }
        foreach (var character in Enumerable.Range(0, 32).Select(n => (char)n)
                     .Concat(new[] { '<', '>', ':', '"', '/', '\\', '|', '?', '*' }))
        {
            var name = $"bad{character}name";
            Add($"Reject invalid character U+{(int)character:X4}", s => InvalidName(s, name));
        }
        foreach (var stem in new[] { "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$" }
                     .Concat("123456789¹²³".SelectMany(n => new[] { $"COM{n}", $"LPT{n}" })))
        {
            Add($"Reject reserved name {stem}", s => InvalidName(s, stem));
            Add($"Reject reserved name with mixed case and extension {stem}", s =>
                InvalidName(s, stem.ToLowerInvariant() + ".txt"));
        }
        Add("Reject reserved stem with space before extension", s => InvalidName(s, "con .txt"));
        Add("Preserved extension does not legalize reserved name", s =>
            Invalid(RenamePlanner.Create([s.File("a.txt", "a")], ["COM¹"], true)));
        Add("Preserved extension does not legalize empty base name", s =>
            Invalid(RenamePlanner.Create([s.File("a.txt", "a")], [""], true)));
        Add("Do not over-reject nonreserved device-like names", s =>
        {
            var source = s.File("a.txt", "a");
            foreach (var name in new[] { "COM0", "COM10", "COM⁴", "LPT0", "LPT10", "CONSOLE", "auxiliary", ".config" })
            {
                Check(RenamePlanner.ValidateName(name) is null, $"Unexpected rejection: {name}");
                Valid(RenamePlanner.Create([source], [name], false));
            }
        });
        Add("Reject total target path at or beyond MAX_PATH", s =>
        {
            var source = s.File("a", "a");
            var length = 260 - (Path.GetDirectoryName(source)!.Length + 1);
            Check(length is > 0 and <= 255, "Test sandbox path leaves no usable boundary name.");
            Invalid(RenamePlanner.Create([source], [new string('x', length)], false));
        });
        Add("Reject duplicate target paths ignoring case", s =>
        {
            var plan = RenamePlanner.Create([s.File("a", "a"), s.File("b", "b")], ["Same", "same"], false);
            Invalid(plan);
            Check(plan.Rows[1].Error is not null, "Duplicate target must mark its row.");
        });
        Add("Reject external file at target", s =>
        {
            var source = s.File("a", "a");
            s.File("occupied", "external");
            Invalid(RenamePlanner.Create([source], ["occupied"], false));
            s.AssertFiles(("a", "a"), ("occupied", "external"));
        });
        Add("Reject external directory at target", s =>
        {
            var source = s.File("a", "a");
            s.File("occupied/child", "external");
            Invalid(RenamePlanner.Create([source], ["occupied"], false));
            s.AssertFiles(("a", "a"), ("occupied/child", "external"));
        });
        Add("Reject duplicate sources ignoring case", s =>
        {
            var source = s.File("source.txt", "a");
            Invalid(RenamePlanner.Create([source, s.PathOf("SOURCE.TXT")], ["one", "two"], false));
        });
        Add("Reject duplicate source with normalized path alias", s =>
        {
            var source = s.File("a", "a");
            Invalid(RenamePlanner.Create([source, Path.Combine(s.DataDirectory, ".", "a")], ["b", "c"], false));
        });
        Add("Reject nonexistent source", s =>
            Invalid(RenamePlanner.Create([s.PathOf("missing")], ["b"], false)));
        Add("Reject directory as source", s =>
        {
            s.File("folder/child", "child");
            Invalid(RenamePlanner.Create([s.PathOf("folder")], ["b"], false));
        });
        Add("Unchanged plan has no validation errors but cannot execute", s =>
        {
            var path = s.File("a.txt", "a");
            var plan = RenamePlanner.Create([path], ["a"], true);
            Check(!plan.CanExecute, "Unchanged plan must not execute.");
            Equal(0, plan.Errors.Count);
            Check(!plan.Entries[0].HasChange, "Exact same name must not be a change.");
            Throws<InvalidOperationException>(() => s.Executor().Execute(plan.Entries));
            s.AssertFiles(("a.txt", "a"));
            s.AssertNoJournals();
        });
        Add("Reject a changed row targeting an unchanged row", s =>
        {
            var a = s.File("a", "a");
            var b = s.File("b", "b");
            Invalid(RenamePlanner.Create([a, b], ["b", "b"], false));
        });

        Add("Execute and undo normal rename with byte-exact contents", s =>
            RoundTrip(s, ["a.txt", "b.txt"], ["new-a.txt", "new-b.txt"]));
        Add("Execute and undo swap", s => RoundTrip(s, ["a", "b"], ["b", "a"]));
        Add("Execute and undo three-way cycle", s => RoundTrip(s, ["a", "b", "c"], ["b", "c", "a"]));
        Add("Execute and undo source-target chain", s => RoundTrip(s, ["a", "b", "c"], ["b", "c", "d"]));
        Add("Execute and undo case-only rename with exact directory-entry spelling", s =>
            RoundTrip(s, ["report.txt"], ["REPORT.txt"]));
        Add("Execute and undo extensionless file", s => RoundTrip(s, ["LICENSE"], ["NOTICE"]));
        Add("Execute and undo Unicode names", s => RoundTrip(s, ["源.txt", "café"], ["猫.txt", "e\u0301"]));
        Add("Execute and undo across multiple source directories", s =>
            RoundTrip(s, ["left/a", "right/a", "left/b"], ["left/shared", "right/shared", "left/a"]));
        Add("Execute and undo mixed unchanged and changed rows", s =>
            RoundTrip(s, ["unchanged", "a", "b"], ["unchanged", "b", "a"]));
        Add("Execute and undo extension-preserving plan", s =>
        {
            var path = s.File("old.TXT", "contents");
            var plan = RenamePlanner.Create([path], ["new"], true);
            Valid(plan);
            var executor = s.Executor();
            var receipt = executor.Execute(plan.Entries);
            s.AssertFiles(("new.TXT", "contents"));
            executor.Undo(receipt);
            s.AssertFiles(("old.TXT", "contents"));
            s.AssertNoJournals();
        });

        foreach (var kind in new[] { "independent", "cycle", "multiple directories" })
        {
            for (var failureAt = 1; failureAt <= 6; failureAt++)
            {
                var position = failureAt;
                Add($"Rollback {kind}: fail {(position <= 3 ? "staging" : "finalization")} move {position}", s =>
                    RollbackOnce(s, kind, position, () => new IOException("Injected one-shot failure")));
            }
        }
        Add("Rollback catches UnauthorizedAccessException", s =>
            RollbackOnce(s, "cycle", 5, () => new UnauthorizedAccessException("Injected access failure")));
        Add("Rollback catches Win32Exception", s =>
            RollbackOnce(s, "cycle", 5, () => new Win32Exception(32, "Injected sharing violation")));
        foreach (var position in new[] { 2, 5 })
        {
            Add($"Persistent failure at move {position} retains recovery journal and all payloads", s =>
                PersistentFailure(s, position));
        }

        Add("Undo refuses replaced identity before undoing any row", s =>
        {
            var a = s.File("a", "A");
            var b = s.File("b", "B");
            var executor = s.Executor();
            var receipt = executor.Execute([new(a, s.PathOf("x")), new(b, s.PathOf("y"))]);
            // Keep the real file alive so Windows cannot recycle its identity for the replacement.
            System.IO.File.Move(s.PathOf("y"), s.PathOf("held-original"));
            s.File("y", "replacement");
            Throws<IOException>(() => executor.Undo(receipt));
            s.AssertFiles(("x", "A"), ("y", "replacement"), ("held-original", "B"));
            s.AssertNoJournals();
        });
        Add("Undo refuses missing current file before undoing any row", s =>
        {
            var a = s.File("a", "A");
            var b = s.File("b", "B");
            var executor = s.Executor();
            var receipt = executor.Execute([new(a, s.PathOf("x")), new(b, s.PathOf("y"))]);
            System.IO.File.Move(s.PathOf("y"), s.PathOf("held-original"));
            Throws<IOException>(() => executor.Undo(receipt));
            s.AssertFiles(("x", "A"), ("held-original", "B"));
            s.AssertNoJournals();
        });
        foreach (var directory in new[] { false, true })
        {
            Add($"Undo refuses original occupied by {(directory ? "directory" : "file")}", s =>
            {
                var a = s.File("a", "A");
                var b = s.File("b", "B");
                var executor = s.Executor();
                var receipt = executor.Execute([new(a, s.PathOf("x")), new(b, s.PathOf("y"))]);
                s.File(directory ? "b/child" : "b", "external");
                var before = s.Snapshot();
                Throws<InvalidOperationException>(() => executor.Undo(receipt));
                s.AssertSnapshot(before);
                s.AssertNoJournals();
            });
        }
        Add("Undo is not reusable after a successful undo", s =>
        {
            var a = s.File("a", "A");
            var executor = s.Executor();
            var receipt = executor.Execute([new(a, s.PathOf("x"))]);
            executor.Undo(receipt);
            Throws<IOException>(() => executor.Undo(receipt));
            s.AssertFiles(("a", "A"));
        });

        foreach (var directory in new[] { false, true })
        {
            Add($"Execution revalidates newly occupied target {(directory ? "directory" : "file")}", s =>
            {
                var plan = RenamePlanner.Create([s.File("a", "A"), s.File("b", "B")], ["x", "y"], false);
                Valid(plan);
                s.File(directory ? "y/child" : "y", "external");
                var before = s.Snapshot();
                var moves = 0;
                var executor = new RenameExecutor(s.RecoveryDirectory, (from, to) =>
                {
                    moves++;
                    System.IO.File.Move(from, to);
                });
                Throws<InvalidOperationException>(() => executor.Execute(plan.Entries));
                Equal(0, moves);
                s.AssertSnapshot(before);
                s.AssertNoJournals();
            });
        }
        Add("Execution revalidates source removed since preview", s =>
        {
            var plan = RenamePlanner.Create([s.File("a", "A"), s.File("b", "B")], ["x", "y"], false);
            Valid(plan);
            System.IO.File.Move(s.PathOf("b"), s.PathOf("held"));
            Throws<InvalidOperationException>(() => s.Executor().Execute(plan.Entries));
            s.AssertFiles(("a", "A"), ("held", "B"));
            s.AssertNoJournals();
        });
        Add("Execution rejects cross-directory entries without moving files", s =>
        {
            var a = s.File("left/a", "A");
            s.File("right/other", "other");
            Throws<InvalidOperationException>(() => s.Executor().Execute([new(a, s.PathOf("right/b"))]));
            s.AssertFiles(("left/a", "A"), ("right/other", "other"));
            s.AssertNoJournals();
        });
        Add("Execution validates hand-built invalid and duplicate targets", s =>
        {
            var a = s.File("a", "A");
            var b = s.File("b", "B");
            Throws<InvalidOperationException>(() => s.Executor().Execute([new(a, s.PathOf("CON.txt"))]));
            Throws<InvalidOperationException>(() => s.Executor().Execute([new(a, s.PathOf("x")), new(b, s.PathOf("X"))]));
            Throws<InvalidOperationException>(() => s.Executor().Execute([new(a, s.PathOf("x")), new(a, s.PathOf("y"))]));
            s.AssertFiles(("a", "A"), ("b", "B"));
            s.AssertNoJournals();
        });
        Add("Staging detects replaced source identity and restores earlier moves", s =>
        {
            var a = s.File("a", "A");
            var b = s.File("b", "B");
            var calls = 0;
            var executor = new RenameExecutor(s.RecoveryDirectory, (from, to) =>
            {
                System.IO.File.Move(from, to);
                if (++calls == 1)
                {
                    System.IO.File.Move(b, s.PathOf("held-original"));
                    s.File("b", "replacement");
                }
            });
            var error = Throws<RenameFailureException>(() => executor.Execute([new(a, s.PathOf("x")), new(b, s.PathOf("y"))]));
            Check(error.RecoveryPath is null, "Earlier staged file should roll back successfully.");
            s.AssertFiles(("a", "A"), ("b", "replacement"), ("held-original", "B"));
            s.AssertNoJournals();
        });
        Add("Target created during staging is not overwritten", s =>
        {
            var a = s.File("a", "A");
            var b = s.File("b", "B");
            var calls = 0;
            var executor = new RenameExecutor(s.RecoveryDirectory, (from, to) =>
            {
                System.IO.File.Move(from, to);
                if (++calls == 2) s.File("y", "external");
            });
            var error = Throws<RenameFailureException>(() => executor.Execute([new(a, s.PathOf("x")), new(b, s.PathOf("y"))]));
            Check(error.RecoveryPath is null, "Rollback should succeed without overwriting the external file.");
            s.AssertFiles(("a", "A"), ("b", "B"), ("y", "external"));
            s.AssertNoJournals();
        });
        Add("Rollback refuses a replaced staged identity and keeps its journal", s =>
        {
            var a = s.File("a", "A");
            var b = s.File("b", "B");
            string? staged = null;
            var calls = 0;
            var executor = new RenameExecutor(s.RecoveryDirectory, (from, to) =>
            {
                if (++calls == 2)
                {
                    System.IO.File.Move(staged!, s.PathOf("held-original"));
                    System.IO.File.WriteAllText(staged!, "replacement");
                    throw new IOException("Injected failure after staged identity replacement");
                }
                System.IO.File.Move(from, to);
                staged = to;
            });
            var error = Throws<RenameFailureException>(() => executor.Execute([new(a, s.PathOf("x")), new(b, s.PathOf("y"))]));
            AssertJournal(s, error, 2);
            Check(!System.IO.File.Exists(a), "Rollback must not move the replacement to the original name.");
            Equal("replacement", System.IO.File.ReadAllText(staged!));
            s.AssertFiles((Path.GetFileName(staged!), "replacement"), ("held-original", "A"), ("b", "B"));
        });
        Add("Finalization refuses staged identity replaced after staging", FinalizationIdentityRegression);
    }

    private static void InvalidName(Sandbox s, string name)
    {
        Check(RenamePlanner.ValidateName(name) is not null, "ValidateName accepted an unsafe filename.");
        var source = s.File("source.txt", "untouched");
        var plan = RenamePlanner.Create([source], [name], false);
        Invalid(plan);
        Check(plan.Rows[0].Error is not null, "Unsafe filename must mark its row.");
        s.AssertFiles(("source.txt", "untouched"));
    }

    private static void RoundTrip(Sandbox s, string[] sources, string[] targets)
    {
        var original = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var renamed = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        for (var i = 0; i < sources.Length; i++)
        {
            // Includes every byte value as well as distinct per-file data, not just text.
            var bytes = Encoding.UTF8.GetBytes($"payload-{i}-猫\r\n").Concat(Enumerable.Range(0, 256).Select(n => (byte)n)).ToArray();
            s.Bytes(sources[i], bytes);
            original.Add(Sandbox.Normalize(sources[i]), bytes);
            renamed.Add(Sandbox.Normalize(targets[i]), bytes);
        }
        var plan = RenamePlanner.Create(sources.Select(s.PathOf).ToArray(), targets.Select(Path.GetFileName).Select(n => n!).ToArray(), false);
        Valid(plan);
        SequenceEqual(targets.Select(s.PathOf), plan.Entries.Select(e => e.TargetPath));
        var executor = s.Executor();
        var receipt = executor.Execute(plan.Entries);
        var changes = plan.Entries.Where(e => e.HasChange).ToArray();
        Equal(changes.Length, receipt.Entries.Count);
        Equal(changes.Length, receipt.Entries.Select(e => e.Identity).Distinct().Count());
        for (var i = 0; i < changes.Length; i++)
        {
            Equal(changes[i].SourcePath, receipt.Entries[i].OriginalPath);
            Equal(changes[i].TargetPath, receipt.Entries[i].CurrentPath);
        }
        s.AssertSnapshot(renamed);
        s.AssertNoJournals();
        executor.Undo(receipt);
        s.AssertSnapshot(original);
        s.AssertNoJournals();
    }

    private static RenameEntry[] FailureEntries(Sandbox s, string kind)
    {
        var sources = kind == "multiple directories" ? new[] { "left/a", "right/b", "left/c" } : new[] { "a", "b", "c" };
        var targets = kind switch
        {
            "cycle" => new[] { "b", "c", "a" },
            "multiple directories" => new[] { "left/x", "right/y", "left/z" },
            _ => new[] { "x", "y", "z" }
        };
        return sources.Select((source, index) => new RenameEntry(s.File(source, $"payload-{index}-猫"), s.PathOf(targets[index]))).ToArray();
    }

    private static void RollbackOnce(Sandbox s, string kind, int failureAt, Func<Exception> failure)
    {
        var entries = FailureEntries(s, kind);
        var before = s.Snapshot();
        var calls = 0;
        var injected = failure();
        var executor = new RenameExecutor(s.RecoveryDirectory, (from, to) =>
        {
            if (++calls == failureAt) throw injected;
            System.IO.File.Move(from, to);
        });
        var error = Throws<RenameFailureException>(() => executor.Execute(entries));
        Check(ReferenceEquals(injected, error.InnerException), "Failure should preserve the injected cause.");
        Check(calls >= failureAt, "Injected failure was not reached.");
        Check(error.RecoveryPath is null, "Successful rollback should not leave a recovery path.");
        s.AssertSnapshot(before);
        s.AssertNoJournals();
        // A successful rollback must leave the batch usable, not merely readable.
        var retry = s.Executor();
        retry.Undo(retry.Execute(entries));
        s.AssertSnapshot(before);
        s.AssertNoJournals();
    }

    private static void PersistentFailure(Sandbox s, int failureAt)
    {
        var entries = FailureEntries(s, "cycle");
        var before = s.Snapshot();
        var calls = 0;
        var executor = new RenameExecutor(s.RecoveryDirectory, (from, to) =>
        {
            if (++calls >= failureAt) throw new IOException("Injected persistent move failure");
            System.IO.File.Move(from, to);
        });
        var error = Throws<RenameFailureException>(() => executor.Execute(entries));
        Check(calls > failureAt, "Rollback must have been attempted.");
        Check(error.InnerException is AggregateException aggregate && aggregate.InnerExceptions.Count >= 2,
            "Persistent failure must report original and rollback errors.");
        AssertJournal(s, error, entries.Length);
        var survivors = s.Snapshot();
        Equal(before.Count, survivors.Count);
        foreach (var bytes in before.Values)
            Equal(1, survivors.Values.Count(value => value.SequenceEqual(bytes)), "Every original payload must survive exactly once.");
        using var journal = JsonDocument.Parse(System.IO.File.ReadAllText(error.RecoveryPath!));
        for (var i = 0; i < entries.Length; i++)
        {
            var item = journal.RootElement[i];
            Equal(entries[i].SourcePath, item.GetProperty("OriginalPath").GetString());
            Equal(entries[i].TargetPath, item.GetProperty("TargetPath").GetString());
            var candidates = new[] { "OriginalPath", "TargetPath", "TemporaryPath" }
                .Select(property => item.GetProperty(property).GetString()!).Distinct(StringComparer.OrdinalIgnoreCase);
            var payload = before[Path.GetRelativePath(s.DataDirectory, entries[i].SourcePath)];
            Check(candidates.Any(path => System.IO.File.Exists(path) && System.IO.File.ReadAllBytes(path).SequenceEqual(payload)),
                "Journal must locate each surviving original payload.");
        }
    }

    private static void AssertJournal(Sandbox s, RenameFailureException error, int count)
    {
        Check(error.RecoveryPath is not null, "Incomplete rollback must expose a recovery path.");
        Check(System.IO.File.Exists(error.RecoveryPath), "Recovery journal must exist.");
        Equal(s.RecoveryDirectory, Path.GetDirectoryName(error.RecoveryPath));
        using var document = JsonDocument.Parse(System.IO.File.ReadAllText(error.RecoveryPath!));
        Equal(count, document.RootElement.GetArrayLength());
        foreach (var operation in document.RootElement.EnumerateArray())
        {
            foreach (var property in new[] { "OriginalPath", "TargetPath", "TemporaryPath" })
            {
                var path = operation.GetProperty(property).GetString()!;
                Check(Path.IsPathFullyQualified(path) && path.StartsWith(s.DataDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase),
                    "Journal path must identify a file within this sandbox.");
            }
            _ = operation.GetProperty("Identity").GetProperty("Volume").GetUInt32();
            _ = operation.GetProperty("Identity").GetProperty("Index").GetUInt64();
        }
    }

    private static void FinalizationIdentityRegression(Sandbox s)
    {
        var a = s.File("a", "A");
        var b = s.File("b", "B");
        string? stagedA = null;
        var calls = 0;
        var executor = new RenameExecutor(s.RecoveryDirectory, (from, to) =>
        {
            System.IO.File.Move(from, to);
            calls++;
            if (calls == 1) stagedA = to;
            if (calls == 2)
            {
                // Simulate another process replacing an already-staged file between phases.
                System.IO.File.Move(stagedA!, s.PathOf("held-original"));
                System.IO.File.WriteAllText(stagedA!, "replacement");
            }
        });
        RenameFailureException? rejected = null;
        RenameReceipt? receipt = null;
        try { receipt = executor.Execute([new(a, s.PathOf("x")), new(b, s.PathOf("y"))]); }
        catch (RenameFailureException error) { rejected = error; }

        Equal("A", System.IO.File.ReadAllText(s.PathOf("held-original")));
        if (rejected is null)
        {
            s.AssertFiles(("held-original", "A"), ("x", "replacement"), ("y", "B"));
            s.AssertNoJournals();
            Throws<IOException>(() => executor.Undo(receipt!));
            throw new TestFailure("Execute reported success and removed its journal after moving a replacement staged file; the returned receipt cannot be undone. Finalization must revalidate identity.");
        }
        AssertJournal(s, rejected, 2);
        Check(!System.IO.File.Exists(s.PathOf("x")), "Replacement staged file must not be finalized.");
        Equal("replacement", System.IO.File.ReadAllText(stagedA!));
        Equal("B", System.IO.File.ReadAllText(b));
    }

    private static void Valid(RenamePlan plan)
    {
        Check(plan.CanExecute, "Expected executable plan: " + string.Join("; ", plan.Errors));
        Equal(0, plan.Errors.Count);
        Check(plan.Rows.All(row => row.Error is null), "Valid plan must not contain row errors.");
    }

    private static void Invalid(RenamePlan plan)
    {
        Check(!plan.CanExecute, "Unsafe plan must not execute.");
        Check(plan.Errors.Count > 0, "Invalid plan must explain the rejection.");
    }

    internal static void Check(bool condition, string message)
    {
        if (!condition) throw new TestFailure(message);
    }

    internal static void Equal<T>(T expected, T actual, string? message = null) =>
        Check(EqualityComparer<T>.Default.Equals(expected, actual), message ?? $"Expected <{expected}>, got <{actual}>.");

    internal static void SequenceEqual<T>(IEnumerable<T> expected, IEnumerable<T> actual) =>
        Check(expected.SequenceEqual(actual), $"Sequences differ. Expected [{string.Join(", ", expected)}]; actual [{string.Join(", ", actual)}].");

    private static T Throws<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T error) { return error; }
        catch (Exception error) { throw new TestFailure($"Expected {typeof(T).Name}, got {error.GetType().Name}.", error); }
        throw new TestFailure($"Expected {typeof(T).Name}, but no exception was thrown.");
    }
}

internal sealed class TestFailure(string message, Exception? inner = null) : Exception(message, inner);

internal sealed class Sandbox : IDisposable
{
    // Keep all generated files inside this test project's output tree, never the user's files.
    private readonly string root = Path.Combine(AppContext.BaseDirectory, "lr-test-" + Guid.NewGuid().ToString("N"));
    public string DataDirectory => Path.Combine(root, "files");
    public string RecoveryDirectory => Path.Combine(root, "recovery");

    public Sandbox() => Directory.CreateDirectory(DataDirectory);
    public static string Normalize(string path) => path.Replace('/', Path.DirectorySeparatorChar);
    public string PathOf(string relative) => Path.Combine(DataDirectory, Normalize(relative));
    public RenameExecutor Executor() => new(RecoveryDirectory);

    public string File(string relative, string contents) => Bytes(relative, Encoding.UTF8.GetBytes(contents));

    public string Bytes(string relative, byte[] contents)
    {
        var path = PathOf(relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        System.IO.File.WriteAllBytes(path, contents);
        return path;
    }

    public Dictionary<string, byte[]> Snapshot() => Directory.EnumerateFiles(DataDirectory, "*", SearchOption.AllDirectories)
        .ToDictionary(path => Path.GetRelativePath(DataDirectory, path), System.IO.File.ReadAllBytes, StringComparer.Ordinal);

    public void AssertFiles(params (string Path, string Content)[] expected) => AssertSnapshot(expected.ToDictionary(
        item => Normalize(item.Path), item => Encoding.UTF8.GetBytes(item.Content), StringComparer.Ordinal));

    public void AssertSnapshot(IReadOnlyDictionary<string, byte[]> expected)
    {
        var actual = Snapshot();
        Program.SequenceEqual(expected.Keys.Order(StringComparer.Ordinal), actual.Keys.Order(StringComparer.Ordinal));
        foreach (var (path, contents) in expected)
            Program.Check(contents.SequenceEqual(actual[path]), $"Bytes changed for {path}.");
    }

    public void AssertNoJournals() => Program.Check(!Directory.Exists(RecoveryDirectory) ||
        !Directory.EnumerateFileSystemEntries(RecoveryDirectory).Any(), "Unexpected recovery journal remains.");

    public void Dispose()
    {
        // This exact GUID directory was created by this instance; never sweep other temp directories.
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }
}
