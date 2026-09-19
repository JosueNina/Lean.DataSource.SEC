## Requesting Data

SEC Whales carries Form 13F institutional holdings today. To add it to your algorithm, call the
**AddData** method with the `Symbol` of an Equity you already subscribe to. Save a reference to the dataset **Symbol** so you
can access the data later in your algorithm.

```python
class SEC13FDataAlgorithm(QCAlgorithm):

    def initialize(self) -> None:
        # Coverage runs 2013-05-20 to 2026-05-29
        self.set_start_date(2020, 10, 1)
        self.set_end_date(2020, 12, 31)
        self.set_cash(100000)

        self._symbol = self.add_equity("AAPL", Resolution.DAILY).symbol
        self._dataset_symbol = self.add_data(SEC13FHoldings, self._symbol).symbol
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
        _datasetSymbol = AddData<SEC13FHoldings>(_symbol).Symbol;
    }
}
```

## Accessing Data

To get the current Form 13F data, index the current [Slice](https://www.quantconnect.com/docs/v2/writing-algorithms/key-concepts/time-modeling/timeslices)
with the dataset **Symbol**. **Slice** objects deliver unique events to your algorithm as they
happen, but the **Slice** may not contain data for your dataset at every time step. Positions are
reported quarterly, but the managers of one quarter file across roughly fifty different days, so
publication is close to continuous: a widely held name such as AAPL carries filings on 58 of the 66
weekdays of the fourth quarter of 2020. Check that the **Slice** contains the data you want before
you index it.

A point is a `SEC13FHoldings` collection holding every position reported for that security on that
filing date, so iterate it rather than reading a single value off it.

```python
def on_data(self, slice: Slice) -> None:
    if self._dataset_symbol in slice:
        for holding in slice[self._dataset_symbol]:
            self.log(f"CIK {holding.manager_cik}: {holding.amount} {holding.amount_type}")
```
```csharp
public override void OnData(Slice slice)
{
    if (slice.ContainsKey(_datasetSymbol))
    {
        SEC13FHoldings point = slice[_datasetSymbol];
        foreach (SEC13FHolding holding in point)
        {
            Log($"CIK {holding.ManagerCik}: {holding.Amount} {holding.AmountType}");
        }
    }
}
```

To iterate through all of the dataset objects in the current **Slice**, call the **Get** method. A
filing deadline puts thousands of securities into the same day, so this is the usual shape when you
subscribe to more than one name.

```python
def on_data(self, slice: Slice) -> None:
    for dataset_symbol, holdings in slice.get(SEC13FHoldings).items():
        for holding in holdings:
            self.log(f"{dataset_symbol} {holding.manager_cik}: {holding.amount}")
```
```csharp
public override void OnData(Slice slice)
{
    foreach (var kvp in slice.Get<SEC13FHoldings>())
    {
        var datasetSymbol = kvp.Key;
        foreach (SEC13FHolding holding in kvp.Value)
        {
            Log($"{datasetSymbol} {holding.ManagerCik}: {holding.Amount}");
        }
    }
}
```

A point's `Time` is the filing date and its `EndTime` is midnight that night. LEAN emits a point at
its end time, so a day's filings reach your algorithm at 00:00 the following day, after EDGAR has
finished listing that day at about 22:05 ET, so a backtest never reads a filing before it existed.
A filing whose EDGAR index came days late is added to history under its filing date.

The filing date is not the quarter the position describes, and the gap between them cannot be
derived: measured across 11,761 filings in one window it runs minimum 0 days, p10 16, median 42, p90
48, maximum 6,596, and 10.4 percent of filings arrive later than the 45 day deadline. A point that
arrives today therefore carries positions from a quarter that ended typically 45 to 135 days ago,
with late amendments arriving years later, and one day of filings can carry several different
reported quarters. Read `PeriodEnd` whenever you need the quarter a position describes rather than
the day it arrived.

### Nothing is aggregated

The dataset publishes what each manager filed. It states no holder count, no total shares and no
total value, because no 13F filing states any of them. Counting is the algorithm's job and it is a
few lines:

```python
def on_data(self, slice: Slice) -> None:
    for dataset_symbol, holdings in slice.get(SEC13FHoldings).items():
        managers = {holding.manager_cik for holding in holdings
                    if holding.put_call is None and holding.amount_type == "SH"}
        shares = sum(holding.amount or 0 for holding in holdings
                     if holding.put_call is None and holding.amount_type == "SH")
        self.log(f"{dataset_symbol}: {len(managers)} managers filed, {shares} shares")
```
```csharp
public override void OnData(Slice slice)
{
    foreach (var kvp in slice.Get<SEC13FHoldings>())
    {
        var shareLines = kvp.Value.Cast<SEC13FHolding>()
            .Where(holding => !holding.PutCall.HasValue && holding.AmountType == "SH")
            .ToList();
        var managers = shareLines.Select(holding => holding.ManagerCik).Distinct().Count();
        var shares = shareLines.Sum(holding => holding.Amount.GetValueOrDefault());
        Log($"{kvp.Key}: {managers} managers filed, {shares} shares");
    }
}
```

Two things to know when you count. A manager files once per quarter on a day of its own choosing,
so breadth builds up across filing dates rather than appearing on any single one: accumulate across
points instead of reading one day. And a single manager can report the same security on more than
one line when the investment discretion differs, which the rules allow, so records outnumber
managers and counting distinct `ManagerCik` is not the same as counting records.

Filter on `AmountType` and `PutCall` before you add anything up. A PRN line is a principal amount of
debt, not a share count, and an option line states the shares underlying the contracts rather than a
holding of the security. Adding either into a share total misstates the position.

### Reading the value

`ReportedValue` is the number the manager wrote, in whatever unit the filing used, and `ValueScale`
is the power of ten that turns it into dollars: 3 for a filing stating thousands, 0 for one stating
dollars, and -3 for a line that overstated its value a thousandfold. `MarketValue` applies the scale
and is what you want in almost every case.

```python
value = holding.market_value      # dollars
raw = holding.reported_value      # as filed, with holding.value_scale beside it
```
```csharp
var value = holding.MarketValue;  // dollars
var raw = holding.ReportedValue;  // as filed, with holding.ValueScale beside it
```

The collection's own `Value` is zero and carries no meaning: LEAN builds the collection and sets
only its symbol and timestamps, so there is nothing for a single number to be. Read the records.

### Amendments and confidential filings

`FormType` is 13F-HR for a holdings report and 13F-HR/A for an amendment, with `AmendmentType`
saying whether the amendment restates the whole report or only adds holdings, and
`AmendmentNumber` its sequence. An amendment is published beside the filing it restates and
replaces nothing, so if your strategy wants a restatement to supersede an earlier figure it has to
apply it. Amendments are rare: 24 of the 1,462 filings carrying positions in the week of 3 August
2026.

`ConfidentialOmitted` is true when the submission withheld other positions under confidential
treatment. Such a filing is incomplete by design and the withheld positions surface in a later
filing, so treat a flagged record as a floor rather than the full picture. `DateReported` carries
the date a previously confidential filing was originally made, and is null on the roughly 998
filings in a thousand that were never confidential.

### Empty values

A field the filing left empty is null, and null is different from a reported zero: a manager
reporting no shared voting authority files a zero, while one that withheld the figure files nothing.
Guard the arithmetic.

```python
shares = holding.amount or 0
```
```csharp
var shares = holding.Amount.GetValueOrDefault();
```

## Historical Data

To get historical Form 13F data, call the **History** method with the
dataset **Symbol**. If there is no data in the period you request, the history result is empty.

```python
# pandas Series, one entry per filing date, each holding the list of that day's positions
history_series = self.history(self._dataset_symbol, timedelta(days=60), Resolution.DAILY)

# DataFrame, one row per reported position
history_df = self.history(SEC13FHoldings, self._dataset_symbol, timedelta(days=60), Resolution.DAILY, flatten=True)

# Dataset objects, one per filing date
history_bars = self.history[SEC13FHoldings](self._dataset_symbol, timedelta(days=60), Resolution.DAILY)
```
```csharp
var history = History<SEC13FHoldings>(_datasetSymbol, TimeSpan.FromDays(60), Resolution.Daily);
```

The three shapes differ more than usual for this dataset, because a point is a collection.

Without `flatten`, Python gives you a **Series** and not a DataFrame: one entry per filing date,
indexed by symbol and time, each entry holding the list of that day's positions. Reading a column
off it will not work, because it has none.

With `flatten=True` you get a DataFrame with **one row per reported position**, indexed by time and
symbol, whose columns are the record's fields in lower case: `accessionnumber`, `managercik`,
`managername`, `periodend`, `formtype`, `amendmenttype`, `amendmentnumber`, `titleofclass`,
`amount`, `amounttype`, `reportedvalue`, `valuescale`, `marketvalue`, `putcall`, `investmentdiscretion`, `othermanager`,
`votingsole`, `votingshared`, `votingnone`, `confidentialomitted`, `datereported`. This is the form
to use for anything cross-sectional. Sixty days of AAPL history is one Series of 39 entries or a
DataFrame of 6,333 rows, which is the difference the flag makes.

Note that a reported zero occasionally comes back as `NaN` in the flattened frame rather than as
`0`, which is LEAN's pandas conversion rather than a gap in the data. Treat the two alike:

```python
shares = history_df["votingshared"].fillna(0)
```

The typed history and the C# history give you the `SEC13FHoldings` objects themselves, one per
filing date, each carrying its records. That is the form that keeps the day's positions grouped.

Ask for a time span rather than a bar count. A bar count is read in daily bars and the dataset
publishes on filing dates only, so what comes back depends on how widely the security is held rather
than on the count you asked for. AAPL carries filings on 58 of the 66 weekdays of the fourth quarter
of 2020, while a thinly held name carries them on a handful of days a year.

For more information about historical data, see [History Requests](https://www.quantconnect.com/docs/v2/writing-algorithms/historical-data/history-requests).

## Remove Subscriptions

To remove your subscription to SEC Whales data, call the **RemoveSecurity**
method.

```python
self.remove_security(self._dataset_symbol)
```
```csharp
RemoveSecurity(_datasetSymbol);
```

If you subscribe to SEC Whales data for assets in a dynamic universe,
remove the dataset subscription when the asset leaves your universe. To view a common design
pattern, see [Track Security Changes](https://www.quantconnect.com/docs/v2/writing-algorithms/algorithm-framework/alpha/key-concepts#05-Track-Security-Changes).
