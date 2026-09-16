## Requesting Data

To add SEC Form 13F Institutional Holdings data to your algorithm, call the **AddData** method with
the `Symbol` of an Equity you already subscribe to. Save a reference to the dataset **Symbol** so you
can access the data later in your algorithm.

```python
class SEC13FDataAlgorithm(QCAlgorithm):

    def initialize(self) -> None:
        # Coverage runs 2013-05-20 to 2026-05-29
        self.set_start_date(2020, 10, 1)
        self.set_end_date(2020, 12, 31)
        self.set_cash(100000)

        self._symbol = self.add_equity("AAPL", Resolution.DAILY).symbol
        self._dataset_symbol = self.add_data(SEC13F, self._symbol).symbol
```
```csharp
public class SEC13FDataAlgorithm : QCAlgorithm
{
    private Symbol _symbol, _datasetSymbol;

    public override void Initialize()
    {
        // Coverage runs 2013-05-20 to 2026-05-29
        SetStartDate(2020, 10, 1);
        SetEndDate(2020, 12, 31);
        SetCash(100000);

        _symbol = AddEquity("AAPL", Resolution.Daily).Symbol;
        _datasetSymbol = AddData<SEC13F>(_symbol).Symbol;
    }
}
```

## Accessing Data

To get the current SEC Form 13F Institutional Holdings data, index the current [Slice](https://www.quantconnect.com/docs/v2/writing-algorithms/key-concepts/time-modeling/timeslices)
with the dataset **Symbol**. **Slice** objects deliver unique events to your algorithm as they
happen, but the **Slice** may not contain data for your dataset at every time step. Positions are
reported quarterly, but the managers of one quarter file across roughly fifty different days, so
publication is close to continuous: measured on the processed files a security carries a release on
a median of 111 days a year, and a widely held name on 234 of the roughly 250 business days. Check
that the **Slice** contains the data you want before you index it.

```python
def on_data(self, slice: Slice) -> None:
    if self._dataset_symbol in slice:
        data_point = slice[self._dataset_symbol]
        holders = data_point.most_reported.holders
        shares = data_point.most_reported.shares
```
```csharp
public override void OnData(Slice slice)
{
    if (slice.ContainsKey(_datasetSymbol))
    {
        var dataPoint = slice[_datasetSymbol];
        var holders = dataPoint.MostReported.Holders;
        var shares = dataPoint.MostReported.Shares;
    }
}
```

To iterate through all of the dataset objects in the current **Slice**, call the **Get** method. A
filing deadline puts thousands of securities into the same day, so this is the usual shape when you
subscribe to more than one name.

```python
def on_data(self, slice: Slice) -> None:
    for dataset_symbol, data_point in slice.get(SEC13F).items():
        holders = data_point.most_reported.holders
```
```csharp
public override void OnData(Slice slice)
{
    foreach (var kvp in slice.Get<SEC13F>())
    {
        var datasetSymbol = kvp.Key;
        var dataPoint = kvp.Value;
        var holders = dataPoint.MostReported.Holders;
    }
}
```

A data point's `Time` and `EndTime` are both the moment it was published, 03:00 ET on the day after
the filing date, and the quarters the numbers describe are carried inside the point. EDGAR lists a
day's filings at about 22:05 ET and the daily job reads them after midnight, so this is the earliest
a live algorithm can have them, and history uses the same stamp. Your algorithm therefore reads a
position only once the dataset could have delivered it.
The gap between the two is not a constant and cannot be derived: measured across 11,761 filings in
one window it runs minimum 0 days, p10 16, median 42, p90 48, maximum 6,596, and 10.4 percent of
filings arrive later than the 45 day deadline. A point that arrives today therefore describes a
quarter that ended typically 45 to 135 days ago, with late amendments arriving years later, and a
single day of filings carries several different reported quarters. Read `PeriodEnd` whenever you
need the quarter a reading describes rather than the day it arrived.

That last point is why a data point is not a single reading. One release restates every quarter of
the security whose numbers moved that day, and LEAN delivers one data point per security per
timestamp, so the point carries them all in `Holdings`: one `SEC13FHolding` per reported quarter,
oldest first and never empty. Seven percent of the points in the history carry more than one, up to
75. Each holding also exposes `Quarter`, the reported quarter as a sortable label such as `2020Q2`,
derived from `PeriodEnd` so the two can never disagree.

```python
def on_data(self, slice: Slice) -> None:
    if self._dataset_symbol in slice:
        data_point = slice[self._dataset_symbol]
        for holding in data_point.holdings:
            self.log(f"{holding.quarter}: {holding.holders} holders, {holding.shares} shares")
```
```csharp
public override void OnData(Slice slice)
{
    if (slice.ContainsKey(_datasetSymbol))
    {
        SEC13F dataPoint = slice[_datasetSymbol];
        foreach (var holding in dataPoint.Holdings)
        {
            Log($"{holding.Quarter}: {holding.Holders} holders, {holding.Shares} shares");
        }
    }
}
```

Two shortcuts pick a single reading out of the point. `MostReported` is the quarter the most
managers have reported and `Latest` is the newest quarter. They differ during a quarter handover,
which lasts about six weeks: the new quarter has a handful of managers in it while the quarter it
replaces has thousands, so `Latest` is the thin one and `MostReported` is the finished one. Rank a
cross-section on `MostReported`, which compares complete readings; take `Latest` only when you
deliberately want the freshest read and have accounted for how incomplete it is. The point's `Value`
is `MostReported.HoldingValue`.

Every value is cumulative for its `PeriodEnd`, counting every filing for that quarter that was
public by the point's timestamp. You do not have to accumulate anything yourself, and you should
not: adding two points of the same quarter together would double count.

Every numeric field is declared nullable, but every shipped row populates all of them: a quarter
that carries share lines and no option lines reports zero for the option totals rather than leaving
them empty. Zero means the managers reported nothing of that kind, which is a reading and not a gap.
The nullability is there so the type can express a gap if the source ever produces one, so the
defensive check costs nothing, but do not expect `null` in the published history.

```python
holders = data_point.most_reported.holders or 0
shares_per_holder = data_point.most_reported.shares / holders if holders else 0
```
```csharp
var mostReported = dataPoint.MostReported;
var holders = mostReported.Holders ?? 0m;
var sharesPerHolder = holders == 0m ? 0m : mostReported.Shares.GetValueOrDefault() / holders;
```

`ConfidentialOmitted` is true when at least one of the filings behind that quarter withheld
positions under confidential treatment. Such a filing is incomplete by design and the withheld
positions surface in a later release, so treat a flagged reading as a floor on the reported position
rather than as the full picture.

## Historical Data

To get historical SEC Form 13F Institutional Holdings data, call the **History** method with the
dataset **Symbol**. If there is no data in the period you request, the history result is empty.

```python
# DataFrame, one row per release
history_df = self.history(self._dataset_symbol, timedelta(days=730), Resolution.DAILY)

# DataFrame, one row per reported quarter
history_flat_df = self.history(SEC13F, self._dataset_symbol, timedelta(days=730), Resolution.DAILY, flatten=True)

# Dataset objects, one per release
history_bars = self.history[SEC13F](self._dataset_symbol, timedelta(days=730), Resolution.DAILY)
```
```csharp
var history = History<SEC13F>(_datasetSymbol, TimeSpan.FromDays(730), Resolution.Daily);
```

The typed history and the C# history give you the points themselves, one per release, each carrying
its own `Holdings`. A release that restated several quarters is one object, so read `Holdings` to
see them rather than expecting one object per quarter.

In Python the DataFrame has two shapes, and the difference matters. Because a point enumerates its
quarters, passing `flatten=True` expands it into **one row per reported quarter**, which is the form
to use whenever you want to line quarters up or build a per-quarter series. Without it you get **one
row per release**, whose columns are the fields of that release's `MostReported` reading plus a
`holdings` column holding the full list, so a release that restated ten quarters still occupies a
single row and nine of its readings are only reachable through that column.

Ask for a time span rather than a bar count. A bar count is read in daily bars and the dataset
publishes on filing dates only, so what comes back depends on how widely the security is held
rather than on the count you asked for. Measured on the processed files, AAPL carries a release on
96 of the last 100 business days and on 235 of the last 365 calendar days, while a thinly held name
carries one on closer to 40 days a year. A time span is the safer form whenever you need a known
number of quarters of history.

For more information about historical data, see [History Requests](https://www.quantconnect.com/docs/v2/writing-algorithms/historical-data/history-requests).

## Universe Selection

To select a dynamic universe of US Equities based on SEC Form 13F Institutional Holdings data, call
the **AddUniverse** method with the **SEC13FUniverse** class and a selection function. The universe
file is keyed by release date and a single release date carries filings for several reported
quarters, but each security still arrives as a single `SEC13F` record with its quarters travelling
together, so your selection function sees one record per security. At most two quarters are live
per security there, the one still filling in and the one it replaces, and `MostReported` is the
complete one of the two.

```python
def initialize(self) -> None:
    self.universe_settings.resolution = Resolution.DAILY
    self.add_universe(SEC13FUniverse, self._select)

def _select(self, data: List[SEC13F]) -> List[Symbol]:
    # Select the most widely held securities of the release
    ranked = [datum for datum in data if datum.most_reported.holders is not None]
    selected = sorted(ranked,
                      key=lambda datum: (-datum.most_reported.holders, datum.symbol.value))[:20]
    return [datum.symbol for datum in selected]
```
```csharp
public override void Initialize()
{
    UniverseSettings.Resolution = Resolution.Daily;

    AddUniverse<SEC13FUniverse>(data =>
    {
        // Select the most widely held securities of the release
        return (from SEC13F datum in data
                let quarter = datum.MostReported
                where quarter.Holders.HasValue
                orderby quarter.Holders.Value descending, datum.Symbol.Value
                select datum.Symbol).Take(20);
    });
}
```

For more information about dynamic universes, see [Universes](https://www.quantconnect.com/docs/v2/writing-algorithms/universes/key-concepts).

## Remove Subscriptions

To remove your subscription to SEC Form 13F Institutional Holdings data, call the **RemoveSecurity**
method.

```python
self.remove_security(self._dataset_symbol)
```
```csharp
RemoveSecurity(_datasetSymbol);
```

If you subscribe to SEC Form 13F Institutional Holdings data for assets in a dynamic universe,
remove the dataset subscription when the asset leaves your universe. To view a common design
pattern, see [Track Security Changes](https://www.quantconnect.com/docs/v2/writing-algorithms/algorithm-framework/alpha/key-concepts#05-Track-Security-Changes).
