// Copyright (c) Microsoft Corporation.  All Rights Reserved.  See License.txt in the project root for license information.

module FSharp.Core.UnitTests.Linq.ExpressionLambdaConversionTests

open System
open System.Linq.Expressions
open Xunit
open Microsoft.FSharp.Linq.RuntimeHelpers

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
let ``EvaluateQuotation still evaluates a let once`` () =
    let counter = Counter()
    let result = LeafExpressionConverter.EvaluateQuotation <@ let v = counter.Next() in v + v @>
    Assert.Equal(2, result :?> int)
    Assert.Equal(1, counter.Calls)

[<Struct>]
type Tally =
    val mutable Count: int

    member this.Add() =
        this.Count <- this.Count + 1

type Reading = { First: int; Second: int }

let private run (lambda: Expression<Func<int, 'T>>) (argument: int) = lambda.Compile().Invoke argument

[<Fact>]
let ``Let read after a later assignment keeps the value it was bound to`` () =
    let lambda =
        LeafExpressionConverter.QuotationToLambdaExpression
            <@
                Func<int, int>(fun a ->
                    let mutable y = a
                    let x = y + 1
                    y <- 100
                    x + y)
            @>

    Assert.Equal(131, run lambda 30)

[<Fact>]
let ``Let read after a property is set keeps the value it was bound to`` () =
    let view = CustomerView(ViewAge = 1)

    let lambda =
        LeafExpressionConverter.QuotationToLambdaExpression
            <@
                Func<int, int>(fun a ->
                    let before = view.ViewAge
                    view.ViewAge <- before + a
                    before)
            @>

    Assert.Equal(1, run lambda 5)
    Assert.Equal(6, view.ViewAge)

[<Fact>]
let ``Mutable struct changed in place is one value`` () =
    let lambda =
        LeafExpressionConverter.QuotationToLambdaExpression
            <@
                Func<int, int>(fun a ->
                    let mutable tally = Tally()
                    tally.Add()
                    tally.Add()
                    tally.Count + a)
            @>

    Assert.Equal(5, run lambda 3)

[<Fact>]
let ``Object bound by a let and changed is one object`` () =
    let lambda =
        LeafExpressionConverter.QuotationToLambdaExpression
            <@
                Func<int, int>(fun a ->
                    let items = Collections.Generic.List<int>()
                    items.Add a
                    items.Count)
            @>

    Assert.Equal(1, run lambda 7)

[<Fact>]
let ``Object bound by a let and read twice is one object`` () =
    let lambda =
        LeafExpressionConverter.QuotationToLambdaExpression <@ Func<int, bool>(fun _ -> let o = obj () in obj.ReferenceEquals(o, o)) @>

    Assert.True(run lambda 0)

[<Fact>]
let ``Let that is never read is still evaluated`` () =
    let counter = Counter()

    let lambda =
        LeafExpressionConverter.QuotationToLambdaExpression <@ Func<int, int>(fun a -> let unused = counter.Next() in a) @>

    Assert.Equal(4, run lambda 4)
    Assert.Equal(1, counter.Calls)

[<Fact>]
let ``Record fields written out of order are evaluated as written in a query over a sequence`` () =
    let counter = Counter()

    let first =
        LeafExpressionConverter.EvaluateQuotation
            <@ query { for _ in [ 1; 2 ] do select { Second = counter.Next(); First = counter.Next() }; head } @>
        :?> Reading

    Assert.Equal({ First = 2; Second = 1 }, first)
