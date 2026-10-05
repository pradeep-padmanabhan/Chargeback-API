# ADR-0120: Rule condition and required-document JSON formats

Status: **Proposed — needs approval by rule owners before any real rule data is authored** · Date: 2026-09-29

## Context
`scheme_rule_specs.conditions_json` (default `'{}'`) and `required_docs` (default `'[]'`) are `jsonb` columns, but their internal format is undefined. A deterministic engine needs a defined, validated format. This ADR defines a technical format only; it contains no rule values.

## Condition format (implemented in `ConditionParser`)
```json
{ "all": [ <condition>, ... ] }
{ "any": [ <condition>, ... ] }
{ "not": <condition> }
{ "fact": "dispute.currencyCode", "op": "eq", "value": "GBP" }
```

Leaf operators by fact type:

| Operators | Applies to | Value |
|---|---|---|
| `eq`, `neq` | all types | a single value |
| `in`, `notIn` | all types | non-empty array |
| `gt`, `gte`, `lt`, `lte` | number and date facts only | a single value |
| `exists`, `notExists` | all types | none (`value` must be omitted) |

- **Fact:** the fact must be in the catalogue (ADR-0121).
- **Value types:** string, JSON number, `true`/`false`, or a date written as `"yyyy-MM-dd"`.
- **Three-valued logic:**
  - A condition on a missing fact is **Unknown**, not false. `exists` and `notExists` are the exception: they are always decidable.
  - `all`: false if any child is false; otherwise unknown if any child is unknown; otherwise true.
  - `any`: true if any child is true; otherwise unknown if any child is unknown; otherwise false.
  - `not`: swaps true and false; unknown stays unknown.
- **Rejected as invalid rule data:**
  - the column default `{}`, which would otherwise match every case;
  - unknown members or facts;
  - type mismatches;
  - empty lists;
  - more than 10 levels of nesting or more than 200 nodes.

## Required documents format (implemented in `RequiredDocumentsParser`)
```json
[ { "slotName": "…", "required": true, "expectedType": "…" } ]
```
- `slotName`: 1–150 characters, unique within the list. Matches `document_slots.slot_name`.
- `required`: an explicit boolean. Matches `document_slots.is_required`.
- `expectedType`: optional, at most 80 characters. Matches `document_slots.expected_type`.
- An empty array means no documents.

## Engine semantics
- **Candidates:** a rule is a candidate only if `approval_status = 'APPROVED'` and both it and its reason code are effective on the evaluation date. Bounds are inclusive at both ends.
- **Determined:** requires exactly one matching candidate, with no unknown or invalid candidate.
- **Otherwise one of:** `NoApprovedRules`, `NoMatch`, `Ambiguous`, `IncompleteFacts` or `InvalidRuleData`.
- **Versioning:** versions of a rule are distinguished by their effective date windows. Two APPROVED versions with overlapping windows are reported as `Ambiguous`, which is a data error. There is no explicit rule key or version column; a gap for the rule owners to decide.

## Decisions required
- Approve the format, or supply the rule owners' preferred format.
- Agree how rules are authored and validated before approval: tooling, and a dry-run against historical cases.
- Decide whether an explicit rule key and version number should be added to `scheme_rule_specs`.
