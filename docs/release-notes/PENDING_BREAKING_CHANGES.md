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

Inside a lambda handed to a LINQ provider (a query operator over `IQueryable`, or an F# lambda passed as `Expression<Func<..>>`) a let no longer makes a `Block` where leaving it out computes the same:

- its value only reads: a constant, the lambda's parameter or a member of it (`let age = p.Age`), or what a member or method of a string, number or date makes of those (`let upper = p.Name.ToUpper()`);
- or the variable is read once, before anything else runs (`let orders = p.Orders.Count() in orders > 2`).

This covers the lets the compiler introduces for tupled lambda parameters and struct copies. For record and anonymous record fields written out of order, the lets are left out as well:

```fsharp
db.People.Select(fun p -> {| Name = p.Name; Age = p.Age |})
// Was: p => { var Name = p.Name; new <>f__AnonymousType(p.Age, Name) }
// Now: p => new <>f__AnonymousType(p.Age, p.Name)
```

**Impact:** When such a lambda is compiled and run, the fields of a record written out of order are evaluated in declaration order instead of the order they are written in, which shows when they have side effects. A let that only names a member of the lambda's parameter reads that member where the variable is used, and not at all on a path that does not use it.

Every other let keeps its `Block` and is evaluated once, where it is written: a value that is computed by a call and read more than once or by a nested lambda, a `mutable` variable, and every let of a lambda that assigns, sets a property or field, or runs a statement. `EvaluateQuotation` and `QuotationToExpression` are not affected.

### Nested lambdas that read a parameter of the outer lambda are quoted

Inside a lambda handed to a LINQ provider, a lambda passed as `Expression<Func<..>>` that reads a parameter of the outer lambda is now a `Quote` of the converted lambda, as in C#, instead of a call that converts its quotation when the outer tree runs.

```fsharp
db.Customers.Where(fun c -> db.Orders.Any(fun o -> o.CustomerId = c.Id))
// Was: c => Orders.Any(QuotationToLambdaExpression(SubstHelper(<quotation>, [c], [c])))
// Now: c => Orders.Any(o => o.CustomerId == c.Id)
```

**Impact:** When such a tree is compiled and run, the nested lambda reads the outer parameter through a closure where it used to get its value as a constant. A nested lambda that reads no parameter of the outer lambda is converted as before, and so is one that cannot be converted.

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
