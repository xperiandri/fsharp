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

**Impact:** When such a lambda is compiled and run, the value expression is evaluated where the variable is read: once for each read, in the order of the reads, and not on a path that does not read it. That shows when the expression has a side effect, can throw, or depends on something another part of the lambda changes. For a record, the fields are evaluated in declaration order instead of the order they are written in.

A lambda keeps its `Block`, and evaluates each let once where it is written, when it assigns, sets a property or field, or runs a statement; for a `mutable` variable; for a let that is never read, unless its value is a variable or an element of a tuple; and for an object created with `new` that is read more than once. Queries over `seq`, `EvaluateQuotation` and `QuotationToExpression` are not affected.

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

---

## IQueryable Type Preservation 🟡

Tuple projections now preserve the provider's `IQueryable` type.

```fsharp
query { for p in db.Products do select (p.Id, p.Name) }
// Was: EnumerableQuery<_>
// Now: Provider's IQueryable<_>
```

**Impact:** Code checking `is EnumerableQuery<_>` may need adjustment.

---

## FCS API: Query Variables 🟢

Query variables now report `IsCompilerGenerated = true`. Fixes #422 (FS1182 false positives).
