// Copyright (c) Microsoft Corporation.  All Rights Reserved.  See License.txt in the project root for license information.

module FSharp.Core.UnitTests.Linq.QueryStructureScenarios

open System
open System.Linq
open System.Linq.Expressions

type Compiled =
    static member Run(lambda: Expression<Func<int, int>>, argument: int) = lambda.Compile().Invoke argument

let private nested operators =
    operators |> List.map Violation.NestedInCollectionSelector

let private orDefault (items: seq<'T>) =
    if Seq.isEmpty items then Seq.singleton Unchecked.defaultof<'T> else items

let private idOrNone (order: Order) =
    match order with
    | null -> -1
    | order -> order.Id

let private nameOrNone (product: Product) =
    match product with
    | null -> ""
    | product -> product.Name

let private ordersWithLines (data: Data) =
    seq { for c in data.Customers do for o in c.Orders do for l in o.Lines -> c.Name, o.Id, l.Id }

let private customersWithOrders (data: Data) =
    seq { for c in data.Customers do for o in c.Orders -> c.Name, o.Id }

let private leftJoinedOrders (data: Data) =
    seq { for c in data.Customers do for o in orDefault c.Orders -> c.Name, idOrNone o }

let private leftJoinedOrdersAndProducts (data: Data) =
    seq {
        for c in data.Customers do
            for o in orDefault c.Orders do
                for p in orDefault (data.Products |> List.filter (fun p -> p.Id = c.Id)) -> c.Name, idOrNone o, nameOrNone p
    }

let private loops () =
    [ Scenario.rows
          "two for"
          (fun db -> query { for c in db.Customers do for p in db.Products do select (c.Name, p.Name) })
          (fun data -> seq { for c in data.Customers do for p in data.Products -> c.Name, p.Name })
      |> Scenario.flat [ "SelectMany"; "Select" ]

      Scenario.rows
          "two for, where over both"
          (fun db -> query { for c in db.Customers do for o in db.Orders do where (o.CustomerId = c.Id); select (c.Name, o.Id) })
          customersWithOrders
      |> Scenario.flat [ "SelectMany"; "Where"; "Select" ]

      Scenario.rows
          "for, where, for, where"
          (fun db ->
              query {
                  for c in db.Customers do
                  where (c.Age > 26)
                  for o in db.Orders do
                  where (o.CustomerId = c.Id)
                  select (c.Name, o.Id)
              })
          (fun data -> seq { for c in data.Customers do if c.Age > 26 then for o in c.Orders -> c.Name, o.Id })
      |> Scenario.flat [ "Where"; "SelectMany"; "Where"; "Select" ]

      Scenario.rows
          "for over a navigation collection"
          (fun db -> query { for c in db.Customers do for o in c.Orders do select (c.Name, o.Id) })
          customersWithOrders
      |> Scenario.flat [ "SelectMany"; "Select" ]

      Scenario.rows
          "for over two levels of navigation collections"
          (fun db -> query { for c in db.Customers do for o in c.Orders do for l in o.Lines do select (c.Name, o.Id, l.Id) })
          ordersWithLines
      |> Scenario.flat [ "SelectMany"; "SelectMany"; "Select" ]

      Scenario.rows
          "three for"
          (fun db ->
              query {
                  for c in db.Customers do
                  for o in db.Orders do
                  for l in db.Lines do
                  where (o.CustomerId = c.Id && l.OrderId = o.Id)
                  select (c.Name, o.Id, l.Id)
              })
          ordersWithLines
      |> Scenario.flat [ "SelectMany"; "SelectMany"; "Where"; "Select" ]

      Scenario.rows
          "four for"
          (fun db ->
              query {
                  for c in db.Customers do
                  for o in c.Orders do
                  for l in o.Lines do
                  for p in db.Products do
                  where (p.Id = l.ProductId)
                  select (c.Name, l.Id, p.Name)
              })
          (fun data ->
              seq {
                  for c in data.Customers do
                      for o in c.Orders do
                          for l in o.Lines do
                              for p in data.Products do
                                  if p.Id = l.ProductId then yield c.Name, l.Id, p.Name
              })
      |> Scenario.flat [ "SelectMany"; "SelectMany"; "SelectMany"; "Where"; "Select" ]

      Scenario.rows
          "if then between three for"
          (fun db ->
              query {
                  for c in db.Customers do
                  where (c.Age > 26)
                  for o in c.Orders do
                  if o.Total > 4.0 then
                      for l in o.Lines do
                      yield (c.Name, l.Id)
              })
          (fun data ->
              seq {
                  for c in data.Customers do
                      if c.Age > 26 then
                          for o in c.Orders do
                              if o.Total > 4.0 then
                                  for l in o.Lines -> c.Name, l.Id
              })
      |> Scenario.flat [ "Where"; "SelectMany"; "Where"; "SelectMany"; "Select" ]

      Scenario.rows
          "yield! after two for"
          (fun db -> query { for c in db.Customers do for o in c.Orders do yield! o.Lines })
          (fun data -> Seq.ofList data.Lines)
      |> Scenario.flat [ "SelectMany"; "SelectMany" ]

      Scenario.rows
          "for after sortBy"
          (fun db -> query { for c in db.Customers do sortBy c.Age; for p in db.Products do select (c.Name, p.Name) })
          (fun data -> seq { for c in data.Customers |> List.sortBy _.Age do for p in data.Products -> c.Name, p.Name })
      |> Scenario.ordered
      |> Scenario.flat [ "OrderBy"; "SelectMany"; "Select" ]

      Scenario.rows
          "for after join"
          (fun db ->
              query {
                  for c in db.Customers do
                  join o in db.Orders on (c.Id = o.CustomerId)
                  for l in db.Lines do
                  where (l.OrderId = o.Id)
                  select (c.Name, o.Id, l.Id)
              })
          ordersWithLines
      |> Scenario.flat [ "Join"; "SelectMany"; "Where"; "Select" ]

      Scenario.rows
          "let between for"
          (fun db ->
              query { for c in db.Customers do let next = c.Id + 9 in for o in db.Orders do where (o.Id = next); select (c.Name, o.Id) })
          (fun data -> seq { for c in data.Customers do for o in data.Orders do if o.Id = c.Id + 9 then yield c.Name, o.Id })
      |> Scenario.flat [ "SelectMany"; "Where"; "Select" ]

      Scenario.rows
          "if then around for"
          (fun db -> query { for c in db.Customers do if c.Age > 26 then for o in c.Orders do yield (c.Name, o.Id) })
          (fun data -> seq { for c in data.Customers do if c.Age > 26 then for o in c.Orders -> c.Name, o.Id })
      |> Scenario.flat [ "Where"; "SelectMany"; "Select" ]

      Scenario.rows
          "for around if then"
          (fun db -> query { for c in db.Customers do for o in db.Orders do if o.CustomerId = c.Id then yield (c.Name, o.Id) })
          customersWithOrders
      |> Scenario.flat [ "SelectMany"; "Select" ]

      Scenario.rows
          "yield! of a navigation collection"
          (fun db -> query { for c in db.Customers do yield! c.Orders })
          (fun data -> Seq.ofList data.Orders)
      |> Scenario.flat [ "SelectMany" ]

      Scenario.rows
          "distinct and take after two for"
          (fun db -> query { for c in db.Customers do for o in c.Orders do select c.City; distinct; take 1 })
          (fun _ -> seq { "London" })
      |> Scenario.ordered
      |> Scenario.flat [ "SelectMany"; "Select"; "Distinct"; "Take" ]

      Scenario.rows
          "groupBy after two for"
          (fun db -> query { for c in db.Customers do for o in c.Orders do groupBy c.City into g; select (g.Key, g.Count()) })
          (fun data -> seq { for c in data.Customers do for _ in c.Orders -> c.City } |> Seq.countBy id)

      Scenario.value "count over two for" (fun db -> query { for c in db.Customers do for o in c.Orders do count }) (fun _ -> 4)

      Scenario.value
          "exists over two for"
          (fun db -> query { for c in db.Customers do for o in c.Orders do exists (o.Total > 10.0 && c.City = "London") })
          (fun _ -> true) ]

let private subqueries () =
    [ Scenario.rows
          "nested query as the source"
          (fun db -> query { for o in (query { for o in db.Orders do where (o.Total > 4.0); select o }) do select o.Id })
          (fun data -> seq { for o in data.Orders do if o.Total > 4.0 then yield o.Id })
      |> Scenario.flat [ "Where"; "Select" ]

      Scenario.rows
          "nested query as the source of the second for"
          (fun db ->
              query {
                  for c in db.Customers do
                  for o in (query { for o in db.Orders do where (o.CustomerId = c.Id); select o }) do
                  select (c.Name, o.Id)
              })
          customersWithOrders
      |> Scenario.flat [ "SelectMany"; "Select" ]

      Scenario.rows
          "nested query in select"
          (fun db ->
              query { for c in db.Customers do select (c.Name, query { for o in db.Orders do where (o.CustomerId = c.Id); select o.Id }) })
          (fun data -> seq { for c in data.Customers -> c.Name, (seq { for o in c.Orders -> o.Id }).AsQueryable() })

      Scenario.rows
          "second for over a source filtered by a lambda"
          (fun db -> query { for c in db.Customers do for o in db.Orders.Where(fun o -> o.CustomerId = c.Id) do select (c.Name, o.Id) })
          customersWithOrders

      Scenario.rows
          "where with a lambda over another source"
          (fun db -> query { for c in db.Customers do where (db.Orders.Any(fun o -> o.CustomerId = c.Id)); select c.Name })
          (fun data -> seq { for c in data.Customers do if c.Orders.Count > 0 then yield c.Name })

      Scenario.rows
          "where with All over another source"
          (fun db -> query { for c in db.Customers do where (db.Orders.Where(fun o -> o.CustomerId = c.Id).All(fun o -> o.Total > 1.0)); select c.Name })
          (fun data -> seq { for c in data.Customers do if c.Orders |> Seq.forall (fun o -> o.Total > 1.0) then yield c.Name })

      Scenario.rows
          "select with Count over another source"
          (fun db -> query { for c in db.Customers do select (c.Name, db.Orders.Count(fun o -> o.CustomerId = c.Id)) })
          (fun data -> seq { for c in data.Customers -> c.Name, c.Orders.Count })

      Scenario.rows
          "lambda over another source capturing a let"
          (fun db -> query { for c in db.Customers do where (let id = c.Id in db.Orders.Any(fun o -> o.CustomerId = id)); select c.Name })
          (fun data -> seq { for c in data.Customers do if c.Orders.Count > 0 then yield c.Name })

      Scenario.rows
          "lambda nested two levels deep"
          (fun db ->
              query {
                  for c in db.Customers do
                  where (db.Orders.Any(fun o -> o.CustomerId = c.Id && db.Lines.Any(fun l -> l.OrderId = o.Id && l.Quantity > 1)))
                  select c.Name
              })
          (fun data -> seq { for c in data.Customers do if c.Orders |> Seq.exists (fun o -> o.Lines |> Seq.exists (fun l -> l.Quantity > 1)) then yield c.Name })

      Scenario.rows
          "where with a method whose lambda reads nothing of the query"
          (fun db -> query { for c in db.Customers do where (c.Age > Compiled.Run((fun x -> x + 1), 26)); select c.Name })
          (fun data -> seq { for c in data.Customers do if c.Age > 27 then yield c.Name }) ]

let private leftJoins () =
    [ Scenario.rows
          "left join spelled leftOuterJoin"
          (fun db ->
              query {
                  for c in db.Customers do
                  leftOuterJoin o in db.Orders on (c.Id = o.CustomerId) into orders
                  for o in orders do
                  select (c.Name, (if obj.ReferenceEquals(o, null) then -1 else o.Id))
              })
          leftJoinedOrders
      |> Scenario.flat [ "GroupJoin"; "SelectMany"; "Select" ]

      Scenario.rows
          "left join spelled groupJoin and DefaultIfEmpty"
          (fun db ->
              query {
                  for c in db.Customers do
                  groupJoin o in db.Orders on (c.Id = o.CustomerId) into orders
                  for o in orders.DefaultIfEmpty() do
                  select (c.Name, (if obj.ReferenceEquals(o, null) then -1 else o.Id))
              })
          leftJoinedOrders
      |> Scenario.flat [ "GroupJoin"; "SelectMany"; "Select" ]

      Scenario.rows
          "group of a leftOuterJoin counted"
          (fun db ->
              query {
                  for c in db.Customers do
                  leftOuterJoin o in db.Orders on (c.Id = o.CustomerId) into orders
                  select (c.Name, orders.Count())
              })
          (fun data -> seq { for c in data.Customers -> c.Name, max 1 c.Orders.Count })
      |> Scenario.flat [ "GroupJoin"; "Select" ]

      Scenario.rows
          "two left joins spelled leftOuterJoin"
          (fun db ->
              query {
                  for c in db.Customers do
                  leftOuterJoin o in db.Orders on (c.Id = o.CustomerId) into orders
                  for o in orders do
                  leftOuterJoin p in db.Products on (c.Id = p.Id) into products
                  for p in products do
                  select
                      (c.Name,
                       (if obj.ReferenceEquals(o, null) then -1 else o.Id),
                       (if obj.ReferenceEquals(p, null) then "" else p.Name))
              })
          leftJoinedOrdersAndProducts
      |> Scenario.flat [ "GroupJoin"; "SelectMany"; "GroupJoin"; "SelectMany"; "Select" ]

      Scenario.rows
          "two left joins spelled groupJoin and DefaultIfEmpty"
          (fun db ->
              query {
                  for c in db.Customers do
                  groupJoin o in db.Orders on (c.Id = o.CustomerId) into orders
                  for o in orders.DefaultIfEmpty() do
                  groupJoin p in db.Products on (c.Id = p.Id) into products
                  for p in products.DefaultIfEmpty() do
                  select
                      (c.Name,
                       (if obj.ReferenceEquals(o, null) then -1 else o.Id),
                       (if obj.ReferenceEquals(p, null) then "" else p.Name))
              })
          leftJoinedOrdersAndProducts
      |> Scenario.flat [ "GroupJoin"; "SelectMany"; "GroupJoin"; "SelectMany"; "Select" ]

      Scenario.rows
          "join after a left join"
          (fun db ->
              query {
                  for c in db.Customers do
                  leftOuterJoin o in db.Orders on (c.Id = o.CustomerId) into orders
                  for o in orders do
                  join p in db.Products on (c.Id = p.Id)
                  select (c.Name, (if obj.ReferenceEquals(o, null) then -1 else o.Id), p.Name)
              })
          (leftJoinedOrdersAndProducts >> Seq.filter (fun (_, _, product) -> product <> ""))
      |> Scenario.flat [ "GroupJoin"; "SelectMany"; "Join"; "Select" ]

      Scenario.rows
          "two for after a left join"
          (fun db ->
              query {
                  for c in db.Customers do
                  leftOuterJoin o in db.Orders on (c.Id = o.CustomerId) into orders
                  for o in orders do
                  for l in db.Lines do
                  where (not (obj.ReferenceEquals(o, null)) && l.OrderId = o.Id)
                  select (c.Name, o.Id, l.Id)
              })
          ordersWithLines
      |> Scenario.flat [ "GroupJoin"; "SelectMany"; "SelectMany"; "Where"; "Select" ]

      Scenario.rows
          "join after two for"
          (fun db ->
              query {
                  for c in db.Customers do
                  for o in c.Orders do
                  join l in db.Lines on (o.Id = l.OrderId)
                  select (c.Name, o.Id, l.Id)
              })
          ordersWithLines
      |> Scenario.flat [ "SelectMany"; "Join"; "Select" ]

      Scenario.rows
          "join after a for over tuples"
          (fun db ->
              query {
                  for c in db.Customers do
                  for (o, id) in (query { for o in db.Orders do select (o, o.Id) }) do
                  join l in db.Lines on (id = l.OrderId)
                  where (o.CustomerId = c.Id)
                  select (c.Name, id, l.Id)
              })
          ordersWithLines
      |> Scenario.flat [ "SelectMany"; "Join"; "Where"; "Select" ]

      Scenario.rows
          "join on a tuple key after a for over tuples"
          (fun db ->
              query {
                  for c in db.Customers do
                  for (o, id) in (query { for o in db.Orders do select (o, o.Id) }) do
                  join l in db.Lines on ((id, 1) = (l.OrderId, 1))
                  where (o.CustomerId = c.Id)
                  select (c.Name, id, l.Id)
              })
          ordersWithLines
      |> Scenario.flat [ "SelectMany"; "Join"; "Where"; "Select" ]

      Scenario.rows
          "join whose inner key uses an earlier variable"
          (fun db ->
              query {
                  for c in db.Customers do
                  for (o, id) in (query { for o in db.Orders do select (o, o.Id) }) do
                  join l in db.Lines on ((id, o.CustomerId) = (l.OrderId, c.Id))
                  select (c.Name, id, l.Id)
              })
          ordersWithLines
      |> Scenario.violating (nested [ "Join" ])

      Scenario.rows
          "groupJoin after two for"
          (fun db ->
              query {
                  for c in db.Customers do
                  for o in c.Orders do
                  groupJoin l in db.Lines on (o.Id = l.OrderId) into lines
                  select (c.Name, o.Id, lines.Count())
              })
          (fun data -> seq { for c in data.Customers do for o in c.Orders -> c.Name, o.Id, o.Lines.Count })
      |> Scenario.flat [ "SelectMany"; "GroupJoin"; "Select" ]

      Scenario.rows
          "join over a source that uses an earlier variable"
          (fun db ->
              query {
                  for c in db.Customers do
                  for o in c.Orders do
                  join same in c.Orders on (o.Id = same.Id)
                  select (c.Name, same.Id)
              })
          customersWithOrders
      |> Scenario.violating (nested [ "Join" ]) ]

let private finalProjections () =
    let customers (data: Data) = Seq.ofList data.Customers

    [ Scenario.rows
          "tuple selected after where"
          (fun db -> query { for c in db.Customers do where (c.Age > 26); select (c.Id, c.Name) })
          (customers >> Seq.filter (fun c -> c.Age > 26) >> Seq.map (fun c -> c.Id, c.Name))
      |> Scenario.flat [ "Where"; "Select" ]

      Scenario.rows
          "struct tuple selected after where"
          (fun db -> query { for c in db.Customers do where (c.Age > 26); select (struct (c.Id, c.Name)) })
          (customers >> Seq.filter (fun c -> c.Age > 26) >> Seq.map (fun c -> struct (c.Id, c.Name)))
      |> Scenario.flat [ "Where"; "Select" ]

      Scenario.rows
          "record selected after sortBy"
          (fun db -> query { for c in db.Customers do sortBy c.Age; select { PersonName = c.Name; PersonAge = c.Age } })
          (customers >> Seq.sortBy _.Age >> Seq.map (fun c -> { PersonName = c.Name; PersonAge = c.Age }))
      |> Scenario.ordered
      |> Scenario.flat [ "OrderBy"; "Select" ]

      Scenario.rows
          "anonymous record selected after join"
          (fun db -> query { for c in db.Customers do join o in db.Orders on (c.Id = o.CustomerId); select {| Name = c.Name; Total = o.Total |} })
          (fun data -> seq { for c in data.Customers do for o in c.Orders -> {| Name = c.Name; Total = o.Total |} })
      |> Scenario.flat [ "Join"; "Select" ]

      Scenario.rows
          "nested tuple selected"
          (fun db -> query { for c in db.Customers do select ((c.Id, c.Name), c.Age) })
          (customers >> Seq.map (fun c -> (c.Id, c.Name), c.Age))
      |> Scenario.flat [ "Select" ]

      Scenario.rows
          "tuple of nine selected"
          (fun db -> query { for c in db.Customers do select (c.Id, c.Name, c.City, c.Age, c.Id + 100, c.Name.Length, c.Age * 2, c.Id * 7, c.Age - c.Id) })
          (customers >> Seq.map (fun c -> c.Id, c.Name, c.City, c.Age, c.Id + 100, c.Name.Length, c.Age * 2, c.Id * 7, c.Age - c.Id))
      |> Scenario.flat [ "Select" ]

      Scenario.rows
          "tuple selected after groupBy"
          (fun db -> query { for c in db.Customers do groupBy c.City into g; select (g.Key, g.Count()) })
          (customers >> Seq.countBy _.City)
      |> Scenario.flat [ "GroupBy"; "Select" ]

      Scenario.rows
          "tuple selected before distinct"
          (fun db -> query { for c in db.Customers do select (c.City, c.Age > 26); distinct })
          (customers >> Seq.map (fun c -> c.City, c.Age > 26) >> Seq.distinct)
      |> Scenario.flat [ "Select"; "Distinct"; "Select" ]

      Scenario.rows
          "sorted nested query of records selected in a tuple"
          (fun db ->
              query {
                  for c in db.Customers do
                  select (c.Name, query { for o in c.Orders do select {| Id = o.Id; Total = o.Total |} into p; sortByDescending p.Total })
              })
          (customers
           >> Seq.map (fun c -> c.Name, c.Orders |> Seq.sortByDescending _.Total |> Seq.map (fun o -> {| Id = o.Id; Total = o.Total |}))) ]

let private consumers () =
    let totals (data: Data) = data.Orders |> List.map _.Total
    let discounts (data: Data) = data.Lines |> List.filter _.Discount.HasValue |> List.map _.Discount.Value

    [ Scenario.value
          "all over two for"
          (fun db -> query { for c in db.Customers do for o in c.Orders do all (o.Total > 4.0 || c.City = "Paris") })
          (fun data -> data.Customers |> List.forall (fun c -> c.Orders |> Seq.forall (fun o -> o.Total > 4.0 || c.City = "Paris")))

      Scenario.value "sumBy over two for" (fun db -> query { for c in db.Customers do for o in c.Orders do sumBy o.Total }) (totals >> List.sum)

      Scenario.value
          "averageBy after a join"
          (fun db -> query { for c in db.Customers do join o in db.Orders on (c.Id = o.CustomerId); averageBy o.Total })
          (totals >> List.average)

      Scenario.value
          "minByNullable over two for"
          (fun db -> query { for o in db.Orders do for l in o.Lines do minByNullable l.Discount })
          (discounts >> List.min >> System.Nullable)

      Scenario.value
          "sumByNullable after a join"
          (fun db -> query { for o in db.Orders do join l in db.Lines on (o.Id = l.OrderId); sumByNullable l.Discount })
          (discounts >> List.sum >> System.Nullable)

      Scenario.value
          "maxBy after a record select"
          (fun db -> query { for c in db.Customers do select { PersonName = c.Name; PersonAge = c.Age } into p; maxBy p.PersonAge })
          (fun data -> data.Customers |> List.map _.Age |> List.max)

      Scenario.value
          "exists after a record select"
          (fun db -> query { for c in db.Customers do select { PersonName = c.Name; PersonAge = c.Age } into p; exists (p.PersonAge > 30) })
          (fun _ -> true)

      Scenario.value
          "exists after a tuple select"
          (fun db -> query { for c in db.Customers do select (c.City, c.Age) into t; exists (match t with city, age -> city = "Paris" && age < 30) })
          (fun _ -> true)

      Scenario.value
          "count after a tuple select"
          (fun db -> query { for c in db.Customers do select (c.City, c.Age) into t; count })
          (fun data -> data.Customers.Length)
      |> Scenario.flat [ "Select"; "Count" ] ]

let cases () = loops () @ subqueries () @ leftJoins () @ finalProjections () @ consumers ()
