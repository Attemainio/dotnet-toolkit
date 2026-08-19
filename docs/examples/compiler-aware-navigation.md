# Worked example: answering a navigation question

A walkthrough of what the tools actually exchange when Claude answers an ordinary question about C#.

> **The codebase is constructed** — `FeeCalculator` is not a real project — because a self-contained
> example is easier to follow than a slice of somebody's repo. **The response formats are real**: each
> one was validated against live `search_index` / `get_symbol` calls, so the field names, the shape
> codes and the line-selector grammar are exactly what the server emits.
>
> For evidence of behaviour rather than illustration of it, see [the benchmarks](../benchmarks.md),
> which are measured rather than written.

## The question

```text
> Where is invoice fee calculation implemented, who calls it, and what
  breaks if I change its signature?
```

The user names no tools. The `dotnet-read` skill routes the work.

## The answer

```text
FeeCalculator.Calculate(Invoice, Customer) — src/Billing/Fees/FeeCalculator.cs:64-152

It is reached three ways:
  · directly from InvoiceBuilder.ApplyFees            (InvoiceBuilder.cs:88)
  · through IFeeStrategy.Calculate                    (2 implementers)
  · from 14 test methods

Changing the signature breaks 6 production call sites and 14 tests. The
IFeeStrategy path is the risk: TieredFeeStrategy overrides it and never
names Calculate in its own text, so a search-and-replace would miss it.
```

`FeeCalculator.cs` is 1,104 lines. It was never opened.

## The calls behind it

### 1. Find it

One call covers every search term; they are OR-ed and ranked together.

```text
search_index(query: "fee calculate tier", limit: 6)
```

```text
shape: P=params M=members N=nested L=lines O=outline D=doclines C=commentlines A=attributes; absent=none
read: "mem=include:members out=include:bodyOutline (maps a long body to slice; also the cheapest body lease for an edit) code=source:code; absent=default fetch is right"
refs: "R=callers E=callees I=implementations(direct only) V=overrides T=tests; 0 shown where it is the answer, absent=not measured"
modifiers: p=public i=internal t=protected x=private s=static a=abstract v=virtual o=override y=async l=sealed g=partial c=const d=readonly e=extension
items[3]{symbolId,name,kind,file,lines,shape,refs,modifiers,read}:
  sym_4c1e9a77b2d0f318,Billing.Fees.FeeCalculator.Calculate,Method,src/Billing/Fees/FeeCalculator.cs,@64-152,P2-L89-O14-D6,R6-T14,p,out
  sym_71ba0c93ee54d18a,Billing.Fees.TieredFeeStrategy,Type,src/Billing/Fees/TieredFeeStrategy.cs,@9-58,M6-L50-D3,I0,pl,mem
  sym_2ef8ab6104c7d955,Billing.Invoicing.InvoiceBuilder.ApplyFees,Method,src/Billing/Invoicing/InvoiceBuilder.cs,@81-96,P1-L16-O3-D3,R2,x,null
termsWithNoHits[1]: tier
```

Three things are already answered without a second call:

- **Where it is** — `@64-152`, an `@from-to` selector that pastes straight into the next call.
- **What fetching it costs** — `P2-L89-O14-D6`: two parameters, 89 lines, 14 branch points, 6 doc lines.
- **What already uses it** — `R6-T14`: six callers, fourteen tests. This is the dead-code answer too;
  `R0` would mean nothing calls it.

The `read` column names the include to pass next when the default fetch would be wrong: `out` on a
89-line method with 14 branches, `mem` on a type. Each legend is stated once per response, and the
whole column disappears when no row needs it.

`termsWithNoHits` names any search term the result never covered — an absent term is never evidence of
an absent symbol.

### 2. Map the long method without reading it

```text
get_symbol(symbol: "sym_4c1e9a77b2d0f318", include: "bodyOutline")
```

```text
symbolId: sym_4c1e9a77b2d0f318
contentVersion: "decl:5c4f619e8b71|body:36ac837aa4f6"
content:
  kind: Method
  displayString: "decimal FeeCalculator.Calculate(Invoice invoice, Customer customer)"
  containingType:
    symbolId: sym_1f27b0ce34a8d952
    displayString: FeeCalculator
  declarationSites[1]{file,startLine,endLine,signatureLine}:
    src/Billing/Fees/FeeCalculator.cs,58,152,64
  bodyOutline:
    if (invoice.Lines.Count == 0),71,73
    foreach (var line in invoice.Lines),78,124
      switch (line.Kind),81,118
        case LineKind.Subscription,83,91
        case LineKind.Usage,93,108
          foreach (var tier in Tiers),97,105
            if (remaining <= tier.Ceiling),100,104
        case _,110,117
    if (customer.IsExempt),128,131
  modifiers: public
```

`declarationSites` carries `startLine` **58** and `signatureLine` **64** — the six-line gap is the
leading `///` doc comment. Report a location from `signatureLine`; anchor an edit on `startLine`, or
the patch silently drops the docs.

`contentVersion` has two layers, `decl:` and `body:`. A patch that rewrites a body needs a version from
an include that actually served one — which `bodyOutline` does, far more cheaply than `source`.

### 3. Read only the lines that answer the question

```text
get_symbol(symbol: "sym_4c1e9a77b2d0f318", source: "code@97-105")
```

```text
  source:
    @97-105
                foreach (var tier in Tiers)
                {
                    var applicable = Math.Min(remaining, tier.Ceiling);
                    if (remaining <= tier.Ceiling)
                    {
                        total += RoundHalfUp(applicable * tier.Rate);
                        break;
                    }
                    remaining -= applicable;
                }
  sourceLineFormat: compact
  sourceLines: 97-105/64-152
```

Nine lines instead of 1,104.

`sourceLineFormat: compact` is the default and carries no per-line numbers. Pass
`source: "full-exact@97-105"` to get them rendered inline instead, which is what you want when quoting
a location or anchoring an edit:

```text
  source:
    97│             foreach (var tier in Tiers)
    98│             {
    99│                 var applicable = Math.Min(remaining, tier.Ceiling);
   100│                 if (remaining <= tier.Ceiling)
   101│                 {
```

The separator is a box-drawing `│` (U+2502), not `|` or `:`.

### 4. Confirm the dispatch edge

```text
get_references(symbol: "sym_4c1e9a77b2d0f318", direction: "callers")
```

This returns the compiler's own answer — including the `TieredFeeStrategy` override that shares no text
with the call site, and so is invisible to any text search — plus `excludedTextMatches`, the count of
comment and string hits a `grep` would have handed back as real.

## Why this shape

Each response answers the *next* question as well as the current one. The search hit says what fetching
costs, so the fetch is already decided; the outline says where the logic is, so the slice is already
decided. The alternative is fetching wide and discarding most of it, which is what reading the file
does.

Full per-tool documentation: [`docs/tools/`](../tools/).
