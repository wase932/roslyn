﻿// Copyright (c) Microsoft.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

using System;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.Shared.TestHooks;
using Microsoft.CodeAnalysis.Test.Utilities;
using Microsoft.CodeAnalysis.Text;
using Microsoft.VisualStudio.IntegrationTest.Utilities;
using Microsoft.VisualStudio.IntegrationTest.Utilities.Input;
using Roslyn.Test.Utilities;
using Xunit;
using ProjectUtils = Microsoft.VisualStudio.IntegrationTest.Utilities.Common.ProjectUtils;

namespace Roslyn.VisualStudio.IntegrationTests.CSharp
{
    [Collection(nameof(SharedIntegrationHostFixture))]
    public class CSharpCodeActions : AbstractEditorTest
    {
        protected override string LanguageName => LanguageNames.CSharp;

        public CSharpCodeActions(VisualStudioInstanceFactory instanceFactory)
            : base(instanceFactory, nameof(CSharpCodeActions))
        {
        }

        [WpfFact(Skip="https://github.com/dotnet/roslyn/issues/26204"), Trait(Traits.Feature, Traits.Features.CodeActionsGenerateMethod)]
        public void GenerateMethodInClosedFile()
        {
            var project = new ProjectUtils.Project(ProjectName);
            VisualStudio.SolutionExplorer.AddFile(project, "Foo.cs", contents: @"
public class Foo
{
}
");

            SetUpEditor(@"
using System;

public class Program
{
    public static void Main(string[] args)
    {
        Foo f = new Foo();
        f.Bar()$$
    }
}
");

            VisualStudio.Editor.InvokeCodeActionList();
            VisualStudio.Editor.Verify.CodeAction("Generate method 'Foo.Bar'", applyFix: true);
            VisualStudio.SolutionExplorer.Verify.FileContents(project, "Foo.cs", @"
using System;

public class Foo
{
    internal void Bar()
    {
        throw new NotImplementedException();
    }
}
");
        }

        [WpfFact, Trait(Traits.Feature, Traits.Features.CodeActionsAddImport)]
        public void FastDoubleInvoke()
        {
            // We want to invoke the first smart tag and then *immediately * try invoking the next.
            // The next will happen to be the 'Simplify name' smart tag.  We should be able
            // to get it to invoke without any sort of waiting to happen.  This helps address a bug
            // we had where our asynchronous smart tags interfered with asynchrony in VS, which caused
            // the second smart tag to not expand if you tried invoking it too quickly
            SetUpEditor(@"
class Program
{
    static void Main(string[] args)
    {
        Exception $$ex = new System.ArgumentException();
    }
}
");
            VisualStudio.Editor.InvokeCodeActionList();
            VisualStudio.Editor.Verify.CodeAction("using System;", applyFix: true, blockUntilComplete: true);
            VisualStudio.Editor.InvokeCodeActionListWithoutWaiting();
            VisualStudio.Editor.Verify.CodeAction("Simplify name 'System.ArgumentException'", applyFix: true, blockUntilComplete: true);

            VisualStudio.Editor.Verify.TextContains(
                @"
using System;

class Program
{
    static void Main(string[] args)
    {
        Exception ex = new ArgumentException();
    }
}");
        }

        [WpfFact, Trait(Traits.Feature, Traits.Features.CodeActionsInvokeDelegateWithConditionalAccess)]
        public void InvokeDelegateWithConditionalAccessMultipleTimes()
        {
            var markup = @"
using System;
class C
{
    public event EventHandler First;
    public event EventHandler Second;
    void RaiseFirst()
    {
        var temp1 = First;
        if (temp1 != null)
        {
            temp1$$(this, EventArgs.Empty);
        }
    }
    void RaiseSecond()
    {
        var temp2 = Second;
        if (temp2 != null)
        {
            temp2(this, EventArgs.Empty);
        }
    }
}";

            MarkupTestFile.GetSpans(markup, out var text, out ImmutableArray<TextSpan> spans);

            SetUpEditor(markup);
            VisualStudio.Editor.InvokeCodeActionList();
            VisualStudio.Editor.Verify.CodeAction("Delegate invocation can be simplified.", applyFix: true, ensureExpectedItemsAreOrdered: true, blockUntilComplete: true);
            VisualStudio.Editor.PlaceCaret("temp2", 0, 0, extendSelection: false, selectBlock: false);
            VisualStudio.Editor.InvokeCodeActionList();
            VisualStudio.Editor.Verify.CodeAction("Delegate invocation can be simplified.", applyFix: true, ensureExpectedItemsAreOrdered: true, blockUntilComplete: true);
            VisualStudio.Editor.Verify.TextContains("First?.");
            VisualStudio.Editor.Verify.TextContains("Second?.");
        }

        [WpfFact]
        [Trait(Traits.Feature, Traits.Features.EditorConfig)]
        [Trait(Traits.Feature, Traits.Features.CodeActionsFixAllOccurrences)]
        public void ApplyEditorConfigAndFixAllOccurrences()
        {
            var markup = @"
class C
{
    public string first;
    public string second;

    public void Method()
    {
        first = null;
        $$second = null;
    }
}";

            // CodingConventions only sends notifications if a file is open for all directories in the project
            VisualStudio.SolutionExplorer.OpenFile(new ProjectUtils.Project(ProjectName), @"Properties\AssemblyInfo.cs");

            // Switch back to the main document we'll be editing
            VisualStudio.SolutionExplorer.OpenFile(new ProjectUtils.Project(ProjectName), "Class1.cs");

            MarkupTestFile.GetSpans(markup, out var text, out ImmutableArray<TextSpan> spans);
            SetUpEditor(markup);
            VisualStudio.WaitForApplicationIdle(CancellationToken.None);
            VisualStudio.Editor.Verify.CodeActionsNotShowing();

            var editorConfig = @"root = true

[*.cs]
dotnet_style_qualification_for_field = true:warning
";

            VisualStudio.SolutionExplorer.AddFile(new ProjectUtils.Project(ProjectName), ".editorconfig", editorConfig, open: false);

            // We have no way of waiting on the CodingConventions library to send its file system changed notifications
            Thread.Sleep(TimeSpan.FromSeconds(1));

            VisualStudio.WaitForApplicationIdle(CancellationToken.None);
            VisualStudio.Workspace.WaitForAllAsyncOperations(
                FeatureAttribute.Workspace,
                FeatureAttribute.SolutionCrawler,
                FeatureAttribute.DiagnosticService);
            VisualStudio.Editor.InvokeCodeActionList();
            VisualStudio.Editor.Verify.CodeActions(
                new[]
                {
                    "Add 'this.'",
                    "Preview changes",
                    "Document",
                    "Project",
                    "Solution",
                    "Suppress IDE0009",
                    "in Source",
                    "Preview changes",
                    "Document",
                    "Project",
                    "Solution",
                    "in Suppression File",
                    "Preview changes",
                    "Document",
                    "Project",
                    "Solution",
                },
                applyFix: "Add 'this.'",
                fixAllScope: FixAllScope.Project,
                ensureExpectedItemsAreOrdered: true,
                verifyCompleteList: true,
                blockUntilComplete: false);

            var expectedTitle = "Preview Changes - Fix all occurrences";
            VisualStudio.PreviewChangesDialog.VerifyOpen(expectedTitle, Helper.HangMitigatingTimeout);
            VisualStudio.PreviewChangesDialog.ClickApplyAndWaitForFeature(expectedTitle, FeatureAttribute.DiagnosticService);

            Assert.Equal(text.Replace("first =", "this.first =").Replace("second =", "this.second ="), VisualStudio.Editor.GetText());

            VisualStudio.SolutionExplorer.SetFileContents(new ProjectUtils.Project(ProjectName), ".editorconfig", editorConfig.Replace("true:warning", "false:warning"));

            // We have no way of waiting on the CodingConventions library to send its file system changed notifications
            Thread.Sleep(TimeSpan.FromSeconds(1));

            VisualStudio.WaitForApplicationIdle(CancellationToken.None);
            VisualStudio.Workspace.WaitForAllAsyncOperations(
                FeatureAttribute.Workspace,
                FeatureAttribute.SolutionCrawler,
                FeatureAttribute.DiagnosticService);
            VisualStudio.Editor.InvokeCodeActionList();
            VisualStudio.Editor.Verify.CodeActions(
                new[]
                {
                    "Remove 'this' qualification",
                    "Preview changes",
                    "Document",
                    "Project",
                    "Solution",
                    "Suppress IDE0003",
                    "in Source",
                    "Preview changes",
                    "Document",
                    "Project",
                    "Solution",
                    "in Suppression File",
                    "Preview changes",
                    "Document",
                    "Project",
                    "Solution",
                },
                applyFix: "Remove 'this' qualification",
                fixAllScope: FixAllScope.Project,
                ensureExpectedItemsAreOrdered: true,
                verifyCompleteList: true,
                blockUntilComplete: false);

            VisualStudio.PreviewChangesDialog.VerifyOpen(expectedTitle, Helper.HangMitigatingTimeout);
            VisualStudio.PreviewChangesDialog.ClickApplyAndWaitForFeature(expectedTitle, FeatureAttribute.DiagnosticService);

            Assert.Equal(text, VisualStudio.Editor.GetText());
        }

        [WpfFact, Trait(Traits.Feature, Traits.Features.CodeActionsGenerateMethod)]
        public void ClassificationInPreviewPane()
        {
            SetUpEditor(@"
class Program
{
    int Main()
    {
        Foo$$();
    }
}");
            VisualStudio.Editor.InvokeCodeActionList();
            var classifiedTokens = GetLightbulbPreviewClassification("Generate method 'Program.Foo'");
            Assert.True(classifiedTokens.Any(c => c.Text == "void" && c.Classification == "keyword"));
        }

        [WpfFact, Trait(Traits.Feature, Traits.Features.CodeActionsAddImport)]
        public void AddUsingExactMatchBeforeRenameTracking()
        {
            SetUpEditor(@"
public class Program
{
    static void Main(string[] args)
    {
        P2$$ p;
    }
}

public class P2 { }");

            VisualStudio.Editor.SendKeys(VirtualKey.Backspace, VirtualKey.Backspace, "Stream");

            VisualStudio.Editor.InvokeCodeActionList();
            var expectedItems = new[]
            {
                "using System.IO;",
                "System.IO.Stream",
                "Generate class 'Stream' in new file",
                "Generate class 'Stream'",
                "Generate nested class 'Stream'",
                "Generate new type...",
                "Rename 'P2' to 'Stream'",
                "Suppress CS0168",
                "in Source"
            };

            VisualStudio.Editor.Verify.CodeActions(expectedItems, applyFix: expectedItems[0], ensureExpectedItemsAreOrdered: true);
            VisualStudio.Editor.Verify.TextContains("using System.IO;");
        }

        [WpfFact, Trait(Traits.Feature, Traits.Features.CodeActionsGenerateType)]
        public void GFUFuzzyMatchAfterRenameTracking()
        {
            SetUpEditor(@"
namespace N
{
    class Goober { }
}

namespace NS
{
    public class P2
    {
        static void Main(string[] args)
        {
            P2$$ p;
        }
    }
}");
            VisualStudio.Editor.SendKeys(VirtualKey.Backspace, VirtualKey.Backspace,
                "Foober");

            VisualStudio.Editor.InvokeCodeActionList();
            var expectedItems = new[]
            {
                "Rename 'P2' to 'Foober'",
                "Generate type 'Foober'",
                "Generate class 'Foober' in new file",
                "Generate class 'Foober'",
                "Generate nested class 'Foober'",
                "Generate new type...",
                "Goober - using N;",
                "Suppress CS0168",
                "in Source",
            };

            VisualStudio.Editor.Verify.CodeActions(expectedItems, applyFix: expectedItems[0], ensureExpectedItemsAreOrdered: true);
        }

        [WpfFact, Trait(Traits.Feature, Traits.Features.CodeGeneration)]
        public void SuppressionAfterRefactorings()
        {
            SetUpEditor(@"
[System.Obsolete]
class C
{
}
class Program
{
    static void Main(string[] args)
    {
        C p = $$2;
    }
}");
            VisualStudio.Editor.SelectTextInCurrentDocument("2");

            VisualStudio.Editor.InvokeCodeActionList();

            var generateImplicitTitle = "Generate implicit conversion operator in 'C'";
            var expectedItems = new[]
            {
                "Introduce constant for '2'",
                "Introduce constant for all occurrences of '2'",
                "Introduce local constant for '2'",
                "Introduce local constant for all occurrences of '2'",
                "Extract Method",
                generateImplicitTitle,
                "Suppress CS0612",
                "in Source",
            };

            VisualStudio.Editor.Verify.CodeActions(expectedItems, applyFix: generateImplicitTitle, ensureExpectedItemsAreOrdered: true);
            VisualStudio.Editor.Verify.TextContains("implicit");
        }

        [WpfFact, Trait(Traits.Feature, Traits.Features.CodeActionsAddImport)]
        public void OrderFixesByCursorProximityLeft()
        {
            SetUpEditor(@"
using System;
public class Program
{
    static void Main(string[] args)
    {
        Byte[] bytes = null;
        GCHandle$$ handle = GCHandle.Alloc(bytes, GCHandleType.Pinned);
    }
}");
            VisualStudio.Editor.InvokeCodeActionList();
            var expectedItems = new[]
            {
                "using System.Runtime.InteropServices;",
                "System.Runtime.InteropServices.GCHandle"
            };

            VisualStudio.Editor.Verify.CodeActions(expectedItems, applyFix: expectedItems[0], ensureExpectedItemsAreOrdered: true);
            VisualStudio.Editor.Verify.TextContains("using System.Runtime.InteropServices");
        }

        [WpfFact, Trait(Traits.Feature, Traits.Features.CodeActionsAddImport)]
        public void OrderFixesByCursorProximityRight()
        {
            SetUpEditor(@"
using System;
public class Program
{
    static void Main(string[] args)
    {
        Byte[] bytes = null;
        GCHandle handle = GCHandle.Alloc(bytes, GCHandleType.$$Pinned);
    }
}");
            VisualStudio.Editor.InvokeCodeActionList();
            var expectedItems = new[]
            {
                "using System.Runtime.InteropServices;",
                "System.Runtime.InteropServices.GCHandle"
            };

            VisualStudio.Editor.Verify.CodeActions(expectedItems, applyFix: expectedItems[0], ensureExpectedItemsAreOrdered: true);
            VisualStudio.Editor.Verify.TextContains("using System.Runtime.InteropServices");

        }
    }
}
