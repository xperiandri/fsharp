---
title: Pending breaking changes
category: Release Notes
categoryindex: 600
index: 5
---
# Breaking Changes in Query Expression Fixes

## AnonymousObject Structural Equality 🔴

`AnonymousObject<T>` types now implement `Equals`/`GetHashCode` with structural equality.

**Impact:** Code using these as Dictionary keys with identity semantics will see different behavior.

```fsharp
let a = AnonymousObject(1, 2)
let b = AnonymousObject(1, 2)
a.Equals(b)  // Was: false, Now: true
```

**Why:** Fixes #7885, #47 - join/groupBy with tuple keys now work correctly.

---

## Expression Tree Structure Changes 🟡

### F# operators on primitive types become what C# has

```fsharp
<@ fun (x: float) -> abs x @>
// Was: Call(Operators.Abs$W, ToFSharpFunc(..), x)
// Now: Call(Math.Abs, x)
```

The same holds for `sqrt`, `floor`, `ceil`, `round`, `truncate`, `sign`, `exp`, `log`, `log10`, the trigonometric functions, `min`, `max` and `**`; `fst` and `snd` read `Item1` and `Item2`, `isNull` compares with a null constant, and `box` is a conversion.

**Impact:** A compiled tree gives what the F# function gives in code. For `min` and `max` on `float` and `float32` that differs from before when an argument is NaN or the arguments are zeros of different sign: the tree used to call the generic comparison, where code calls `Math.Min` and `Math.Max`. A provider that translates `Math.Round` rounds midpoints its own way; SQLite rounds them away from zero. `min` and `max` on `decimal` reach EF Core as `Math.Min` and `Math.Max`, which it translates, where it could not translate the generic comparison; SQLite stores decimals as text and compares them as text, so `Where(fun x -> max x.D 10m = 10m)` over `2.5m` and `-1.25m` returns one row instead of two, with the SQL that `Math.Max` gives in C#.

Together with the change to tuples and records in #20897, a `leftOuterJoin` that follows two `for`s and a `join`, and tests the joined row with `isNull`, fails in EF Core 10 for SQL Server with "Nullable object must have a value": `for c in db.Customers do for o in c.Orders do join d in db.Customers on (c.City = d.City); leftOuterJoin p in db.Products on (d.Id = p.Id) into products; for p in products do select (c.Name, o.Id, d.Name, (if isNull p then "" else p.Name))`. EF Core translates it with either change alone.

### Let-bindings use Block instead of Lambda.Invoke

```fsharp
<@ let x = 1 in x + 1 @>
// Was: Lambda(x => x+1).Invoke(1)
// Now: Block({ x = 1; x + 1 })
```

**Migration:** If you detected let-bindings via `Lambda.Invoke`:
```csharp
// Old: if (node.Method.Name == "Invoke" && node.Object is LambdaExpression)
// New: if (node.NodeType == ExpressionType.Block)
```

Inside a lambda handed to a LINQ provider (a query operator over `IQueryable`, or an F# lambda passed as `Expression<Func<..>>`) a let no longer makes a `Block` where leaving it out computes the same:

- its value only reads: a constant, the lambda's parameter or a member of it (`let age = p.Age`), what a member or method of a string, number or date makes of those (`let upper = p.Name.ToUpper()`), an addition, subtraction or multiplication of those (`let next = p.Age + 1`), or a division by a constant other than 0 and -1 (`let decade = p.Age / 10`);
- or the variable is read once, as the first thing the rest of the lambda evaluates (`let orders = p.Orders.Count() in orders > 2`).

This covers the lets the compiler introduces for tupled lambda parameters and struct copies. For record and anonymous record fields written out of order, the compiler adds a let for each field it moves, and that let is treated like any other:

```fsharp
db.People.Select(fun p -> {| Name = p.Name; Age = p.Age |})
// Was: p => { var Name = p.Name; new <>f__AnonymousType(p.Age, Name) }
// Now: p => new <>f__AnonymousType(p.Age, p.Name)
```

**Impact:** A let that only reads, such as `let age = p.Age`, `let upper = p.Name.ToUpper()` or `let next = p.Age + 1`, is computed where the variable is used. When such a lambda is compiled and run, that changes when and how often it runs:

- once for each use, and once for each element when a nested lambda uses it, an arithmetic read like `let next = p.Age + 1` included: with `names` a `List<string>`, `custs.Select(fun c -> let n = c.Name in names.Count(fun s -> s = n))` reads `Name` once for each name and customer, where it read it once for each customer before;
- at the moment of that use, so a member of the lambda's parameter that something in between changes gives the new value: `fun (items: List<int>) -> let before = items.Count in addTo items 5 + before` gives 2 for an empty list where it gave 1;
- not at all on a path that does not use it: `let s = name.Substring 5 in if name.Length > 5 then s else ""` gives `""` for `"ab"` where it raised `ArgumentOutOfRangeException`.

A record field written out of order whose value is more than a read, such as `{ Second = counter.Next(); First = counter.Next() }`, keeps its let, and the fields are evaluated in the order they are written, as they are in code.

A record that a `query` selects with its fields written out of order is converted like one with its fields in order: the query selects an anonymous object in its place. `distinct` then compares the fields of the record, also for a record type with an `Equals` of its own, as it already did when the fields were written in order. With `[<CustomEquality>] type HeadOnly = { Head: int; Tail: int }` that is equal when `Head` is, `select { Tail = x + 1; Head = 0 }` followed by `distinct` gives one row for each value of `Tail`, where it gave one row.

Every other let keeps its `Block` and is evaluated once, where it is written: a value that is computed by a call and read more than once, by a nested lambda or after something else has run, a struct of your own or a boxed value, a `mutable` variable, and every let of a lambda that assigns, sets a property or field, or runs a statement. A value that a call makes anew, like the `let upper = p.Name.ToUpper()` above, is kept as well when the string goes to more than one place that can tell one object from another, or to one in a nested lambda: `obj.ReferenceEquals(upper, upper)` does, and so do `upper.Substring(0)` and `upper.Trim()`, which can give back the string they are called on, and `fun _ -> upper` in a nested lambda, which gives the same string for every element. A method or a member of a string that gives a number or a boolean, like `upper.StartsWith "A"` or `upper.Length`, only looks at what the string holds, and so does a string that goes to one place only, like `upper + upper`: those are made at each use, also in a nested lambda. A let outside such a lambda keeps its `Block` in `EvaluateQuotation` and `QuotationToExpression`; inside a lambda that goes to a provider it is replaced the same way whichever of them converts it: `EvaluateQuotation <@ people.Select(fun x -> let n = x.Name in n + n).ToArray() @>` reads `Name` twice.

### Nested lambdas that read a parameter of the outer lambda are quoted

Inside a lambda handed to a LINQ provider, a lambda passed as `Expression<Func<..>>` that reads a parameter of the outer lambda is now a `Quote` of the converted lambda, as in C#, instead of a call that converts its quotation when the outer tree runs.

```fsharp
db.Customers.Where(fun c -> db.Orders.Any(fun o -> o.CustomerId = c.Id))
// Was: c => Orders.Any(QuotationToLambdaExpression(SubstHelper(<quotation>, [c], [c])))
// Now: c => Orders.Any(o => o.CustomerId == c.Id)
```

**Impact:** When such a tree is compiled and run, the nested lambda reads the outer parameter through a closure where it used to get its value as a constant. A let of the outer lambda that names a member of its parameter is read by the nested lambda when that lambda runs. A nested lambda that reads no parameter of the outer lambda is converted as before, and so is one that cannot be converted.

### Array access uses ArrayIndex

```fsharp
<@ arr.[0] @>
// Was: Call(GetArray, arr, 0)
// Now: ArrayIndex(arr, 0)
```

**Migration:**
```csharp
// Old: if (node.Method.Name == "GetArray")
// New: if (node.NodeType == ExpressionType.ArrayIndex)
```

### Tuple and record constructions name their members

Constructing a tuple, struct tuple, record or anonymous record now gives a `NewExpression` whose `Members` say which property (or `ValueTuple` field) each argument becomes, as C# anonymous types do, so providers can bind a later read such as `t.Item1` or `r.Name` to the argument.

```fsharp
<@ fun (c: Customer) -> (c.Id, c.Name) @>
// Was: new Tuple`2(c.Id, c.Name)
// Now: new Tuple`2(Item1 = c.Id, Item2 = c.Name)
```

A record whose fields have no public getter is still built without `Members`.

The SQL that a provider writes names its columns after the members (`SELECT "b"."Id" AS "Item1", ...`), which shows in tests that compare SQL text.

A `groupBy` on an item of a tuple that an earlier operator selected, `for (city, id) in (select (b.Address.City, b.Id)) do groupBy city into g; select (g.Key, g.Count())`, failed in EF Core with "could not be translated" and now fails inside EF Core with `KeyNotFoundException: The given key 'Item1' was not present in the dictionary`. It translates together with the change to groupings in #20899.

Together with the change to F# operators in #20900, a `leftOuterJoin` that follows two `for`s and a `join`, and tests the joined row with `isNull`, fails in EF Core 10 for SQL Server with "Nullable object must have a value": `for c in db.Customers do for o in c.Orders do join d in db.Customers on (c.City = d.City); leftOuterJoin p in db.Products on (d.Id = p.Id) into products; for p in products do select (c.Name, o.Id, d.Name, (if isNull p then "" else p.Name))`. EF Core translates it with either change alone.

---

## IQueryable Type Preservation 🟡

Tuple projections now preserve the provider's `IQueryable` type.

```fsharp
query { for p in db.Products do select (p.Id, p.Name) }
// Was: EnumerableQuery<_>
// Now: Provider's IQueryable<_>
```

**Impact:** Code checking `is EnumerableQuery<_>` may need adjustment.

When the last operator of a query selects a tuple or record, the provider now gets one `Select` that builds it directly:

```fsharp
query { for p in db.Products do select (p.Id, p.Name) }
// Was: Products.Select(p => new AnonymousObject`2(Item1 = p.Id, Item2 = p.Name)).Select(after => new Tuple`2(after.Item1, after.Item2))
// Now: Products.Select(p => new Tuple`2(p.Id, p.Name))
```

After a `join`, a second `for` or a `groupBy` over tuples, the query no longer re-projects each element into a copy of itself, and a group used later in the query is read from the provider's grouping:

```fsharp
query { for c in db.Customers do join o in db.Orders on (c.Id = o.CustomerId); groupBy c.City into g; select (g.Key, g.Count()) }
// Was: ...Join(..).Select(_arg1 => new AnonymousObject`2(Item1 = _arg1.Item1, Item2 = _arg1.Item2)).GroupBy(..)
//        .Select(_arg2 => new Grouping`2(_arg2.Key, _arg2.Select(v => new Tuple`2(v.Item1, v.Item2)))).Select(g => ..g.Key..g.Count()..)
// Now: ...Join(..).GroupBy(..).Select(g => new AnonymousObject`2(Item1 = g.Key, Item2 = g.Count()))
```

**Impact:** A group of tuples or records that a query uses as a value in the middle of a query, for example one that is passed on whole to a later `select` or `where`, is now built at each place where the query mentions it. FSharp.Core 10.1 built it once, in the `Select` right after the `GroupBy`, so every later operator saw one object; now two mentions are two objects. Over an in-memory `IQueryable`, such as `AsQueryable()` over a list, a query that compares a group with itself gives other rows:

```fsharp
query { for c in customers.AsQueryable() do
        groupValBy (c.Name, c.Age) c.City into g
        where (g.Equals g)
        select (g.Key, obj.ReferenceEquals(g, g)) }
// Was: every city, with true
// Now: no rows (without the where: every city, with false)
```

In the same way, `Contains` no longer finds a group among the groups it is in, and a group used as the key of `groupBy` is no longer found among the elements of its own group. EF Core and the Cosmos DB provider translate none of these queries, before or after. linq2db translated none of them before. After, it translates the `Contains` query and gives the rows of LINQ to Objects; it still translates neither of the other two. Reading the key of a group, or counting, summing, averaging or searching it, is not affected: those read the provider's grouping.

---

## FCS API: Query Variables 🟢

Query variables now report `IsCompilerGenerated = true`. Fixes #422 (FS1182 false positives).
