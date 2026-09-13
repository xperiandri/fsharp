// Copyright (c) Microsoft Corporation.  All Rights Reserved.  See License.txt in the project root for license information.

/// Go To Implementation over an F# library and a C# project referencing its built assembly.
module FSharp.Editor.Tests.FindImplementationsTests

open System
open System.Collections
open System.IO
open System.Reflection
open System.Threading
open Xunit
open Microsoft.CodeAnalysis
open Microsoft.CodeAnalysis.ExternalAccess.FSharp.Editor.FindUsages
open Microsoft.CodeAnalysis.Text
open Microsoft.VisualStudio.FSharp.Editor
open FSharp.Compiler.CodeAnalysis
open FSharp.Editor.Tests.Helpers
open FSharp.Test.ProjectGeneration

let private shapes =
    """
type IShape =
    abstract Area: float

type IShape2 = IShape

[<AbstractClass>]
type Animal() =
    abstract Speak: unit -> string
    default _.Speak() = "..."

[<Sealed>]
type Rock() =
    member _.Weight = 1
"""

let private implementations =
    """
open ModuleShapes

type Circle() =
    interface IShape with
        member _.Area = 3.14

type Square =
    { Side: float }

    interface IShape with
        member this.Area = this.Side * this.Side

type Triangle() =
    interface IShape2 with
        member _.Area = 0.5

let unitShape =
    { new IShape with
        member _.Area = 1.0 }

type Dog() =
    inherit Animal()
    override _.Speak() = "Woof"

type Puppy() =
    inherit Dog()
    override _.Speak() = "Yip"

type Cat() =
    inherit Animal()
"""

let private library =
    SyntheticProject.Create(
        { sourceFile "Shapes" [] with
            Source = shapes
        },
        { sourceFile "Implementations" [ "Shapes" ] with
            Source = implementations
        }
    )

let private csharpSource =
    $"""using {library.Name};

class CSharpShape : ModuleShapes.IShape
{{
    public double Area => 2.0;
}}

class CSharpDog : ModuleShapes.Animal
{{
    public override string Speak() => "Wuff";
}}
"""

let private solution =
    let librarySolution, checker = RoslynTestHelpers.CreateMultiProjectSolution library
    let assembly = RoslynTestHelpers.CompileToAssembly(library, checker)

    RoslynTestHelpers.AddCSharpProject(librarySolution, "Consumer", csharpSource, library.GetProjectOptions checker, [ assembly ])

let private findUsagesService =
    FSharpFindUsagesService() :> IFSharpFindUsagesService

/// Every result of Go To Implementation at `offset` into the first `marker` of the file: its name, its file and the line
/// it is on, with its span between bars. ExternalAccess exposes neither the name nor the span of a definition item, so
/// its Roslyn item is read through reflection.
let private implementationsAt fileId (marker: string) offset =
    let context, foundDefinitions, foundReferences =
        RoslynTestHelpers.CreateFindUsagesContext()

    let path = library.GetFilePath fileId

    let document =
        solution.GetDocumentIdsWithFilePath path
        |> Seq.exactlyOne
        |> solution.GetDocument

    let position =
        (File.ReadAllText path).IndexOf(marker, StringComparison.Ordinal) + offset

    findUsagesService.FindImplementationsAsync(document, position, context).Wait()
    Assert.Empty foundReferences

    let flags = BindingFlags.Instance ||| BindingFlags.NonPublic ||| BindingFlags.Public

    let property (target: obj) name =
        target.GetType().GetProperty(name, flags).GetValue target

    [
        for definition in foundDefinitions do
            let item = property definition "RoslynDefinitionItem"

            let name =
                [
                    for part in (property item "DisplayParts" :?> IEnumerable) -> (part :?> TaggedText).Text
                ]
                |> String.concat ""

            for documentSpan in (property item "SourceSpans" :?> IEnumerable) do
                let document = property documentSpan "Document" :?> Document
                let span = property documentSpan "SourceSpan" :?> TextSpan
                let text = document.GetTextAsync(CancellationToken.None).Result
                let line = text.Lines.GetLineFromPosition(span.Start)
                let start = span.Start - line.Start
                name, Path.GetFileName document.FilePath, line.ToString().Insert(start + span.Length, "|").Insert(start, "|").Trim()
    ]
    |> List.sort

let private assertImplementations (expected: (string * string * string) list) actual =
    Assert.Equal<(string * string * string) list>(List.sort expected, actual)

[<Fact>]
let ``Go To Implementation on an interface lists the types and object expressions implementing it, in C# too`` () =
    implementationsAt "Shapes" "IShape" 0
    |> assertImplementations
        [
            "Circle", "FileImplementations.fs", "type |Circle|() ="
            "Square", "FileImplementations.fs", "type |Square| ="
            "Triangle", "FileImplementations.fs", "type |Triangle|() ="
            "new IShape", "FileImplementations.fs", "{ new |IShape| with"
            "CSharpShape", "Program.cs", "class |CSharpShape| : ModuleShapes.IShape"
        ]

[<Fact>]
let ``Go To Implementation on an interface property lists the members implementing it, in C# too`` () =
    implementationsAt "Shapes" "Area" 0
    |> assertImplementations
        [
            "Circle.Area", "FileImplementations.fs", "member _.|Area| = 3.14"
            "Square.Area", "FileImplementations.fs", "member this.|Area| = this.Side * this.Side"
            "Triangle.Area", "FileImplementations.fs", "member _.|Area| = 0.5"
            "new IShape.Area", "FileImplementations.fs", "member _.|Area| = 1.0 }"
            "CSharpShape.Area", "Program.cs", "public double |Area| => 2.0;"
        ]

[<Fact>]
let ``Go To Implementation on a class lists the classes deriving from it transitively, in C# too`` () =
    implementationsAt "Shapes" "Animal" 0
    |> assertImplementations
        [
            "Dog", "FileImplementations.fs", "type |Dog|() ="
            "Puppy", "FileImplementations.fs", "type |Puppy|() ="
            "Cat", "FileImplementations.fs", "type |Cat|() ="
            "CSharpDog", "Program.cs", "class |CSharpDog| : ModuleShapes.Animal"
        ]

[<Fact>]
let ``Go To Implementation on an abstract member lists its default and every override, in C# too`` () =
    implementationsAt "Shapes" "Speak" 0
    |> assertImplementations
        [
            "Animal.Speak", "FileShapes.fs", "default _.|Speak|() = \"...\""
            "Dog.Speak", "FileImplementations.fs", "override _.|Speak|() = \"Woof\""
            "Puppy.Speak", "FileImplementations.fs", "override _.|Speak|() = \"Yip\""
            "CSharpDog.Speak", "Program.cs", "public override string |Speak|() => \"Wuff\";"
        ]

[<Fact>]
let ``Go To Implementation on a default member lists the overrides in the classes deriving from its class, in C# too`` () =
    implementationsAt "Shapes" "default _.Speak" "default _.".Length
    |> assertImplementations
        [
            "Dog.Speak", "FileImplementations.fs", "override _.|Speak|() = \"Woof\""
            "Puppy.Speak", "FileImplementations.fs", "override _.|Speak|() = \"Yip\""
            "CSharpDog.Speak", "Program.cs", "public override string |Speak|() => \"Wuff\";"
        ]

[<Fact>]
let ``Go To Implementation on an override lists only the overrides in the classes deriving from its class`` () =
    implementationsAt "Implementations" "override _.Speak() = \"Woof\"" "override _.".Length
    |> assertImplementations [ "Puppy.Speak", "FileImplementations.fs", "override _.|Speak|() = \"Yip\"" ]

[<Theory>]
[<InlineData("Shapes", "Rock", 0, "Rock", "type |Rock|() =")>]
[<InlineData("Implementations", "Square", 0, "Square", "type |Square| =")>]
[<InlineData("Implementations", "override _.Speak() = \"Yip\"", 11, "Speak", "override _.|Speak|() = \"Yip\"")>]
let ``Go To Implementation on what nothing implements goes to its declaration``
    (fileId: string, marker: string, offset: int, name: string, line: string)
    =
    implementationsAt fileId marker offset
    |> assertImplementations [ name, Path.GetFileName(library.GetFilePath fileId), line ]

let private checker = FSharpChecker.Create()

[<Theory>]
[<InlineData("type C() =\n    interface IShape with\n        member _.Area = 1.0", "C: IShape")>]
[<InlineData("type D() =\n    inherit Base()", "D: Base")>]
[<InlineData("type G() =\n    inherit Ns.Base<int>()", "G: Ns.Base")>]
[<InlineData("type I2 =\n    inherit IShape", "I2: IShape")>]
[<InlineData("type A = IShape", "A: IShape")>]
[<InlineData("type R =\n    { X: int }\n\n    interface IShape with\n        member _.Area = 1.0", "R: IShape")>]
[<InlineData("let f () =\n    { new IShape with\n        member _.Area = 1.0 }", "IShape: IShape")>]
[<InlineData("let o =\n    { new Base() with\n        member _.M() = ()\n      interface IShape with\n        member _.Area = 1.0 }",
             "Base: Base; Base: IShape")>]
[<InlineData("type P() =\n    member _.X = 1", "")>]
let ``The inheritance sites of a file are the types it names after inherit, interface, new and in an abbreviation``
    (source: string, expected: string)
    =
    let fileName = "test.fs"

    let parseResults =
        checker.ParseFile(
            fileName,
            FSharp.Compiler.Text.SourceText.ofString source,
            { FSharpParsingOptions.Default with
                SourceFiles = [| fileName |]
            }
        )
        |> Async.RunSynchronously

    let actual =
        InheritanceSites.ofParseTree parseResults.ParseTree
        |> Seq.map (fun site -> $"""{site.DeclaredName}: {String.concat "." site.Names}""")
        |> String.concat "; "

    Assert.Equal(expected, actual)
