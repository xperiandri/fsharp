// Copyright (c) Microsoft Corporation.  All Rights Reserved.  See License.txt in the project root for license information.

module FSharp.Core.UnitTests.Linq.ExpressionFSharpOperatorTests

open System
open System.Linq.Expressions
open Xunit
open Microsoft.FSharp.Linq.RuntimeHelpers

type Distance =
    { Metres: float }

    static member Abs(d: Distance) = { Metres = abs d.Metres }

let private bodyOf (quotation: Quotations.Expr<Func<'T, 'U>>) =
    (LeafExpressionConverter.QuotationToLambdaExpression quotation).Body

let private calledMethod (body: Expression) =
    match body with
    | :? MethodCallExpression as call -> call.Method
    | body -> failwith $"Expected a call, got {body}"

[<Fact>]
let ``fst and snd read the tuple's items`` () =
    for body, item in [ bodyOf <@ Func<int * string, int>(fun t -> fst t) @>, "Item1"; bodyOf <@ Func<int * string, string>(fun t -> snd t) @>, "Item2" ] do
        match body with
        | :? MemberExpression as read -> Assert.Equal(item, read.Member.Name)
        | body -> Assert.Fail $"Expected a read of {item}, got {body}"

[<Fact>]
let ``isNull compares with a null constant`` () =
    match bodyOf <@ Func<string, bool>(fun s -> isNull s) @> with
    | :? BinaryExpression as comparison ->
        Assert.Equal(ExpressionType.Equal, comparison.NodeType)
        Assert.Null comparison.Method
        Assert.Equal(ExpressionType.Constant, comparison.Right.NodeType)
    | body -> Assert.Fail $"Expected a comparison, got {body}"

[<Fact>]
let ``box converts to obj`` () =
    let body = bodyOf <@ Func<int, obj>(fun x -> box x) @>
    Assert.Equal(ExpressionType.Convert, body.NodeType)
    Assert.Equal(typeof<obj>, body.Type)

[<Fact>]
let ``Math functions call System.Math`` () =
    Assert.Equal(typeof<Math>.GetMethod("Sqrt", [| typeof<float> |]), calledMethod (bodyOf <@ Func<float, float>(fun x -> sqrt x) @>))
    Assert.Equal(typeof<Math>.GetMethod("Abs", [| typeof<int> |]), calledMethod (bodyOf <@ Func<int, int>(fun x -> abs x) @>))
    Assert.Equal(typeof<Math>.GetMethod("Max", [| typeof<int>; typeof<int> |]), calledMethod (bodyOf <@ Func<int, int>(fun x -> max x 3) @>))
    Assert.Equal(typeof<Math>.GetMethod("Pow", [| typeof<float>; typeof<float> |]), calledMethod (bodyOf <@ Func<float, float>(fun x -> x ** 2.0) @>))

[<Fact>]
let ``Math functions on float32 go through float as the F# functions do`` () =
    let lambda = LeafExpressionConverter.QuotationToLambdaExpression <@ Func<float32, float32>(fun x -> sqrt x) @>

    match lambda.Body with
    | :? UnaryExpression as conversion ->
        Assert.Equal(typeof<float32>, conversion.Type)
        Assert.Equal(typeof<Math>.GetMethod("Sqrt", [| typeof<float> |]), calledMethod conversion.Operand)
    | body -> Assert.Fail $"Expected a conversion to float32, got {body}"

    Assert.Equal(sqrt 2.0f, lambda.Compile().Invoke 2.0f)

[<Fact>]
let ``Math functions on other types keep their F# implementation`` () =
    let absolute = LeafExpressionConverter.QuotationToLambdaExpression <@ Func<Distance, Distance>(fun d -> abs d) @>
    Assert.Equal({ Metres = 2.0 }, absolute.Compile().Invoke { Metres = -2.0 })

    let smaller = LeafExpressionConverter.QuotationToLambdaExpression <@ Func<string, string>(fun s -> min s "b") @>
    Assert.Equal("a", smaller.Compile().Invoke "a")
