// Copyright (c) Microsoft Corporation.  All Rights Reserved.  See License.txt in the project root for license information.

module FSharp.Core.UnitTests.Linq.ExpressionLambdaConversionTests

open System
open System.Linq.Expressions
open Xunit
open Microsoft.FSharp.Linq.RuntimeHelpers

type LinqExpr =
    static member FromFunc<'T, 'U>(e: Expression<Func<'T, 'U>>) = e

type private Counter() =
    let mutable calls = 0
    member _.Calls = calls

    member _.Next() =
        calls <- calls + 1
        calls

let private bodyOf (lambda: Expression<'T>) = lambda.Body

[<Fact>]
let ``Anonymous record with fields out of alphabetical order becomes a New in an expression lambda`` () =
    let lambda = LeafExpressionConverter.QuotationToLambdaExpression <@ Func<int, _>(fun x -> {| B = x; A = x + 1 |}) @>
    Assert.Equal(ExpressionType.New, (bodyOf lambda).NodeType)

[<Fact>]
let ``Record with fields out of declaration order becomes a New in an expression lambda`` () =
    let lambda =
        LeafExpressionConverter.QuotationToLambdaExpression <@ Func<Customer, _>(fun c -> { PersonAge = c.Age; PersonName = c.Name }) @>

    Assert.Equal(ExpressionType.New, (bodyOf lambda).NodeType)

[<Fact>]
let ``Tupled lambda parameters read the tuple directly in an expression lambda`` () =
    let lambda = LeafExpressionConverter.QuotationToLambdaExpression <@ Func<int * string, bool>(fun (id, _) -> id > 1) @>

    match bodyOf lambda with
    | :? BinaryExpression as comparison ->
        match comparison.Left with
        | :? MemberExpression as item -> Assert.Equal("Item1", item.Member.Name)
        | left -> Assert.Fail $"Expected a read of Item1, got {left}"
    | body -> Assert.Fail $"Expected a comparison, got {body}"

[<Fact>]
let ``Assigned mutable variable stays a block in an expression lambda`` () =
    let lambda =
        LeafExpressionConverter.QuotationToLambdaExpression
            <@
                Func<int, int>(fun x ->
                    let mutable y = x
                    y <- y + 1
                    y)
            @>

    Assert.Equal(ExpressionType.Block, (bodyOf lambda).NodeType)
    Assert.Equal(6, lambda.Compile().Invoke 5)

[<Fact>]
let ``Lambda nested in an expression lambda is quoted`` () =
    let lambda =
        LeafExpressionConverter.QuotationToLambdaExpression <@ Func<int, Expression<Func<int, int>>>(fun x -> LinqExpr.FromFunc(fun y -> y + x)) @>

    match bodyOf lambda with
    | :? MethodCallExpression as call ->
        Assert.Equal(nameof LinqExpr.FromFunc, call.Method.Name)
        Assert.Equal(ExpressionType.Quote, call.Arguments.[0].NodeType)
        Assert.Equal(ExpressionType.Lambda, (call.Arguments.[0] :?> UnaryExpression).Operand.NodeType)
    | body -> Assert.Fail $"Expected a call of FromFunc, got {body}"

    Assert.Equal(3, lambda.Compile().Invoke(1).Compile().Invoke 2)

[<Fact>]
let ``Lambda nested in an evaluated quotation is quoted`` () =
    let inner = LeafExpressionConverter.EvaluateQuotation <@ LinqExpr.FromFunc(fun y -> y + 1) @> :?> Expression<Func<int, int>>
    Assert.Equal(ExpressionType.Add, inner.Body.NodeType)
    Assert.Equal(3, inner.Compile().Invoke 2)

[<Fact>]
let ``EvaluateQuotation still evaluates a let once`` () =
    let counter = Counter()
    let result = LeafExpressionConverter.EvaluateQuotation <@ let v = counter.Next() in v + v @>
    Assert.Equal(2, result :?> int)
    Assert.Equal(1, counter.Calls)
