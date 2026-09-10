# SEC Form 13F Institutional Holdings, live trading readiness

Dataset: `SEC13FHoldings` and `SEC13FHoldingsUniverse`, written by `SEC13FDownloader`
(`dataset-name` = `13f`) into `alternative/sec/13f/`.

Every check below was run, not reasoned about. A single FAIL blocks the dataset.

| # | Check | Result |
|---|---|---|
| 1 | Live daily-update simulation | PASS |
| 2 | Point-in-time integrity | PASS |
| 3 | Live-mode `GetSource` | PASS |
| 4 | Reader fires on fresh data | PASS |
| 5 | Resolution compatibility | PASS |
| 6 | Symbols to files integrity | PASS |
| 7 | CI green | NOT RUN, no push yet |

---

## 1. Live daily-update simulation

This is what the QuantConnect Data Fleet runs every day, so passing it is what proves the
live update path rather than only the backtest path.

Ran the processor in incremental mode with `QC_DATAFLEET_DEPLOYMENT_DATE` set, against a
`processed-data-directory` already holding a published history.

```
53 archives published, 2013-04-01..2026-05-31
incremental for 2026-09-08 falls outside every published window,
   reading the newest archive 01mar2026-31may2026_form13f.zip instead
9,716 holdings filings, 2,045 notices skipped
```

- Read the published history: yes. The finalize pass folds
  `processed-data-directory` into the run before writing, which is the single most repeated
  finding in past dataset reviews.
- Fetched only the relevant window: yes, one archive rather than 53, and it says in the log
  which one it chose and why.
- Merged idempotently: **verified three ways, byte for byte across 6,597 files**, with
  `diff -rq` reporting 0 differences.
  1. Clean run, then a rerun with nothing cleared, so the archive pass appended increments
     on top of an already finalized file.
  2. The production shape: run 1's output copied into `processed/`, `temp-output` wiped.
     This is the only one that exercises the difference-and-re-accumulate path, and it
     reproduced run 1 exactly.
  3. A file-level idempotence case inside the unit tests.
- Appended the day's rows: yes, 43 release dates for the 2026 Q1 quarter, one per filing day
  in the archive, and the per-day increments were diffed against a direct read of the raw
  archive with zero discrepancies.
- Fails loudly when the history is not there: an incremental run whose
  `processed-data-directory` is missing, wrong or unmounted now stops before it fetches
  anything, rather than republishing thirteen years of history as the three month window it
  just read and returning success. `FinalizeSecurityFiles` also states how many rows it read
  off the shelf and from where, since the files come out the right shape either way and the
  row count is the only thing that tells a merge from a rebuild.

## 2. Point-in-time integrity

`EndTime` is not a stored field that could drift out of line with the data. Both accessors are
overridden onto the one timestamp the type has:

```csharp
public override DateTime EndTime
{
    get { return Time; }
    set { Time = value; }
}
```

so a point is delivered at the instant it became public and cannot be read earlier than it
existed. There is no publication offset to model, no second timestamp to keep in step, and
no arrangement of the data that could violate the property. Overriding the setter as well as
the getter is what keeps that true when the engine assigns `EndTime`. The quarter the numbers
describe is carried separately in `PeriodEnd`.

That mattered here. The lag from the reported quarter to publication is the product, and it
is not a constant: measured across 11,761 filings it runs minimum 0 days, p10 16, median 42,
p90 48, maximum 6,596, with 10.4 percent arriving later than the 45 day deadline. It is
typically 45 to 135 days, with late amendments arriving years later, and collapsing the two
stamps would have injected all of it as look-ahead.

Asserted in `SEC13FHoldingsTests`: `EndTime` equals `Time` on every parsed row, the release
never precedes the quarter it reports, and the integration test re-checks it over every row
of the real processed output.

## 3. Live-mode `GetSource`

`GetSource(config, date, isLiveMode: true)` returns the same `SubscriptionDataSource` it
returns in backtest: a `LocalFile` at `alternative/sec/13f/<ticker>.csv` with
`FileFormat.Csv`, and `alternative/sec/13f/universe/<yyyyMMdd>.csv` with
`FileFormat.FoldingCollection` for the universe. The method does not branch on `isLiveMode`,
which is correct for a file-backed dataset and matches `FINRAShortInterest`.

Not a streaming dataset: 13F is a file product, so there is no `IDataQueueHandler` to wire.

Both classes do carry `[ProtoContract(SkipConstructor = true)]` and a `[ProtoMember]` on every
stored property, numbered from 10 up because `BaseData` reserves the low numbers. That is not
about streaming: LEAN serializes data points between processes, and a type without the
attributes serializes as nothing at all. It is also not sufficient on its own. LEAN routes a
custom type through the `BaseData` branch of `Extensions.ProtobufSerialize`, and `BaseData`
declares `ProtoInclude` only for Tick, TradeBar, QuoteBar, Dividend and Split, so protobuf-net
answers `Unexpected sub-type` for any data source type, attributed or not. Registration is
LEAN's half of the contract. Ours is asserted by reflection in `SEC13FHoldingsTests`: the
contract is declared, every stored property carries a member number, and the numbers are
unique and start at 10.

Asserted in both test fixtures, including the `FileFormat` on each.

## 4. Reader fires on fresh data

Ran `SEC13FHoldingsAlgorithm` over the shipped `output/` sample, 2020-10-01 to 2020-12-31, and
compared what LEAN delivered against what is on disk.

Last row of `output/alternative/sec/13f/aapl.csv`:

```
20201230 17:30,20200630,3339,2783627775,9904728544000,62440550,61254116,15201000,1721189895,172897459,1
```

Last point delivered to `on_data`:

```
2020-12-30 17:30 AAPL Period 2020-06-30 Holders 3339.0 Shares 2783627775.0
```

Same timestamp, same quarter, same figures. The loop closes end to end: processed, merged,
read by LEAN, delivered to the algorithm, and the run placed 1 order.

**Deliveries reconciled against the files, because they are not one for one.** Over that window
the three files hold 371 rows and `on_data` received 170, which is exactly one per security per
filing date: 58 for AAPL, 56 for GOOGL, 56 for SPY, against 58, 56 and 56 distinct filing dates
on disk. 95 of those dates carry more than one reported quarter, and a `Slice` holds one point
per `Symbol` per timestamp, so the algorithm sees the highest `PeriodEnd` and the other rows are
not reachable from `on_data`. During a handover that survivor is the thin new quarter rather than
the finished one, which is exactly why the universe class carries two quarters explicitly and why
`listing-documentation-13f.md` now says so. `History` returns all 371. This is LEAN's dictionary
semantics, not a defect in the file, but it is not obvious and it was not written down before.

`SEC13FHoldingsUniverseSelectionAlgorithm` was run over the same window and placed 5 orders.

GOOGL arriving is worth its own line. Its CUSIP is absent from LEAN's security database, so
it resolves only through the third step of the identity chain, the N-PORT ticker crosswalk.
Seeing it inside `on_data` proves that step works in the engine and not only in the
processor's log.

## 5. Resolution compatibility

`DefaultResolution()` is `Daily` and `SupportedResolutions()` is Daily only, on both the
per-security class and the universe class. Valid for live: no `Tick` on a non-bar type, and
Daily is the correct resolution for universe data.

The underlying cadence is quarterly with releases on most business days, which is modelled as
Daily and sparse. `IsSparseData()` is true on both classes, so missing-file logs are
suppressed rather than filling the live log.

## 6. Symbols to files integrity

There is no `Symbols` helper to reflect over: this dataset is keyed by resolved `Symbol`
rather than by a fixed list of series. The equivalent guarantee is that every file the
processor writes is readable by the class that claims it, and that is asserted over the real
output rather than over a sample:

```
13f: 13 per-security files, 1087 rows
13f universe: 66 files, 1536 rows
```

Every row parses, `Value` mirrors `HoldingValue`, and the rows are in chronological order.
That last assertion exists because `Accumulate` groups by quarter, which is not the order the
file has to ship in, and LEAN's `SubscriptionDataReader` drops a point whose timestamp moves
backwards without logging anything.

File naming is point-in-time on both sides: the processor resolves the ticker a security
traded under on the filing date through the map files, and the reader asks for
`config.Symbol.Value`, so a renamed company's history splits across files exactly as
`goog.csv` and `googl.csv` do.

## 7. CI

Not run. Nothing has been pushed yet. The fork's first pull request will need a maintainer to
press "Approve and run workflows", since fork PRs do not start the workflow on their own.

---

## Known limits, stated rather than hidden

**Identity resolution is partial by design.** The chain is CUSIP, then the arithmetic US ISIN,
then the N-PORT ticker crosswalk. On the most recent window it reaches 97.9 percent of
reported value. Over the full history it reaches 78.9 percent locally, because the crosswalk
only knows securities that still existed when N-PORT began in late 2019 and the first two
steps need `security-database.csv`, which QuantConnect does not distribute and which is a one
row placeholder outside their environment. Running where that file exists resolves the first
two steps as well and the coverage will be higher than either figure. Phase 0 measured those
two steps alone at 88.9 percent of value in a cloud backtest.

**Steps 1 and 2 of the chain are unexercised locally** for the same reason. They are covered
by unit tests and were measured in the cloud, but the first production run is where they will
be seen working together.
