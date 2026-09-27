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

Inside a lambda handed to a LINQ provider (a query operator over `IQueryable`, or an F# lambda passed as `Expression<Func<..>>`) there is no `Block`: an immutable let is substituted where it is used, as a C# expression lambda would have to be written. This covers the lets the compiler introduces for record and anonymous record fields written out of order, tupled lambda parameters and struct copies.

```fsharp
db.People.Select(fun p -> {| Name = p.Name; Age = p.Age |})
// Was: p => { var Name = p.Name; new <>f__AnonymousType(p.Age, Name) }
// Now: p => new <>f__AnonymousType(p.Age, p.Name)
```

The value expression is repeated when the variable is used more than once, so side effects in it are no longer evaluated exactly once when such a lambda is compiled. A mutable variable that is assigned still produces a `Block`.

### Nested lambdas are quoted

A lambda passed as `Expression<Func<..>>` inside another converted lambda is now a `Quote` of the converted lambda, as in C#, instead of a call that converts its quotation when the outer tree runs.

```fsharp
db.Customers.Where(fun c -> db.Orders.Any(fun o -> o.CustomerId = c.Id))
// Was: c => Orders.Any(QuotationToLambdaExpression(SubstHelper(<quotation>, [c], [c])))
// Now: c => Orders.Any(o => o.CustomerId == c.Id)
```

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
After a `join`, a second `for` or a `groupBy` over tuples, the query no longer re-projects each element into a copy of itself, and a group used later in the query is read from the provider's grouping:

```fsharp
query { for c in db.Customers do join o in db.Orders on (c.Id = o.CustomerId); groupBy c.City into g; select (g.Key, g.Count()) }
// Was: ...Join(..).Select(_arg1 => new AnonymousObject`2(Item1 = _arg1.Item1, Item2 = _arg1.Item2)).GroupBy(..)
//        .Select(_arg2 => new Grouping`2(_arg2.Key, _arg2.Select(v => new Tuple`2(v.Item1, v.Item2)))).Select(g => ..g.Key..g.Count()..)
// Now: ...Join(..).GroupBy(..).Select(g => new AnonymousObject`2(Item1 = g.Key, Item2 = g.Count()))
```

---

## FCS API: Query Variables 🟢

Query variables now report `IsCompilerGenerated = true`. Fixes #422 (FS1182 false positives).
