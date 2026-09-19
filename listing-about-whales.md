## Introduction

SEC Whales is the ownership side of the SEC's filings: who holds what, reported by the holders
themselves. The US Securities and Exchange Commission requires a large holder to say so, and the
filings that carry those disclosures are published through EDGAR as they are made. This product
collects them, one source at a time, and publishes each filing as it was filed.

It ships today with Form 13F institutional holdings. Every institutional investment manager
exercising discretion over at least 100 million dollars must file a Form 13F within 45 days of the
end of a calendar quarter, listing the covered securities it holds, from the second quarter of 2013
to the present. Further ownership filings are added to the product as they are built.

Nothing is summed, counted or averaged. The holders of a name, the shares institutions hold between
them, quarter over quarter change and concentration are all derivable from a day's records, and no
filing states any of them, so they are left to the algorithm rather than invented here. Counting
distinct filer CIKs across the records is a line of code and is what the demonstration algorithms
do.

The reporting lag is the product, not an inconvenience to be hidden. A 13F position is typically 45
to 135 days old by the time it reaches the public record, with late amendments arriving years later,
so a record is stamped with the filing date and carries the quarter it describes in `PeriodEnd`.
Measured across 11,761 filings in one window, the lag from the reported quarter end to the filing
date runs minimum 0 days, p10 16, median 42, p90 48, maximum 6,596, with 10.4 percent of filings
arriving later than the 45 day deadline. Delivering a position on the quarter end it describes would
inject every day of that gap as look-ahead, so LEAN delivers it when the dataset could first publish
it.

A record's `Time` is its filing date and its `EndTime` is midnight that night. LEAN emits a point at
its end time, so a day's filings all reach the algorithm at 00:00 the following day, after EDGAR has
finished listing that day at about 22:05 ET, so a backtest never reads a filing before it existed.
A filing whose EDGAR index came days late is added to history under its filing date.

An algorithm receives one `SEC13FHoldings` point per security per filing date, holding every
position reported for that security that day. Several managers file on the same day, and a single
manager can report the same security on more than one line when the investment discretion differs,
which the rules allow and Berkshire Hathaway does with Moody's. Those records stay apart, because
folding them together would state a number no filing contains.

## About the Provider

The [U.S. Securities and Exchange Commission](https://www.sec.gov) is the federal agency that
regulates the US securities markets. Form 13F is filed through EDGAR, the SEC's electronic filing
system, and the agency's Division of Economic and Risk Analysis republishes those filings as
structured, tab separated data sets in three month batches. The history is built from those data
sets. The daily job reads each day's filings from EDGAR itself, the information tables the data sets
are extracted from, because a batch arrives up to three months after its first filing: compared line
by line, 48 of 48 filings carry the same lines in both, and on six sample days EDGAR's daily index
lists exactly the filings the data set holds. The data is public domain, needs no account and no API
key, and the only access requirement is the descriptive User-Agent header that SEC policy asks of
all automated readers.

## Getting Started

```python
self._symbol = self.add_equity("AAPL", Resolution.DAILY).symbol
self._holdings_symbol = self.add_data(SEC13FHoldings, self._symbol).symbol
```
```csharp
_symbol = AddEquity("AAPL", Resolution.Daily).Symbol;
_holdingsSymbol = AddData<SEC13FHoldings>(_symbol).Symbol;
```

## Data Summary

The following table describes the dataset properties:

| Property | Value |
| --- | --- |
| Start Date | May 2013 |
| Asset Coverage | 8,520 US Equities |
| Data Density | Sparse |
| Resolution | Daily\* |
| Timezone | America/New_York |

\* Positions are reported quarterly, but the managers of one quarter file across roughly fifty
different days and several quarters are live at once, so publication is close to continuous rather
than quarterly. In the week of 3 to 7 August 2026, 1,462 filings carrying positions produced 34,002
security days across 8,520 securities.

The history begins on 2013-05-20, the first filing date in the SEC structured data set, whose first
archive covers the second quarter of 2013. Anything earlier exists only as raw filings in the EDGAR
full index and is not part of this dataset.

Each record in a point carries the following fields, exactly as the manager filed them:

| Property | Meaning |
| --- | --- |
| `AccessionNumber` | EDGAR accession number of the submission the line was reported on |
| `ManagerCik` | Central Index Key of the filing manager, its stable identity across name changes |
| `ManagerName` | The manager's name as its most recent cover page states it, null for an unknown CIK |
| `PeriodEnd` | End of the quarter the position is reported for, the SEC PERIODOFREPORT |
| `FormType` | 13F-HR for a holdings report, 13F-HR/A for an amendment |
| `AmendmentType` | On an amendment, whether it restates the whole report or only adds holdings |
| `AmendmentNumber` | Sequence number of the amendment, null on an original filing |
| `TitleOfClass` | Class of the security as the manager titled it, such as COM or CL A |
| `Amount` | Size of the position: a share count when `AmountType` is SH, a principal amount when PRN |
| `AmountType` | SH for shares, PRN for a principal amount |
| `ReportedValue` | Market value exactly as the manager stated it, in the unit the filing used |
| `ValueScale` | The power of ten that turns `ReportedValue` into dollars: 3, 0 or -3 |
| `MarketValue` | `ReportedValue` in whole dollars, derived from the two above and never stored |
| `PutCall` | Call or Put when the line is an option on the security, null when it is the security |
| `InvestmentDiscretion` | SOLE, DFND or OTR, as the filing states it |
| `OtherManager` | The other managers sharing the position, as the cover page numbers them, separated by semicolons; empty when there are none |
| `VotingSole` | Shares over which the manager holds sole voting authority |
| `VotingShared` | Shares over which the manager shares voting authority |
| `VotingNone` | Shares over which the manager holds no voting authority |
| `ConfidentialOmitted` | True when the submission withheld other positions under confidential treatment |
| `DateReported` | For a previously confidential filing, the date it was originally made |

The CUSIP is not published, being licensed. It resolves the security and stops there.

Option and debt lines are published as what they are rather than folded away. One quarter of the
source carries 61,400 call lines, 57,443 put lines and 15,698 PRN lines, and a share count that
swallowed any of them would misstate the position, so `AmountType` and `PutCall` say what each line
is and the algorithm decides what to count.

`ConfidentialOmitted` is a point-in-time feature rather than a data quality flag. A manager can ask
the SEC to withhold specific positions temporarily, so a filing marked this way is incomplete by
design and the withheld positions appear in a later filing.

### The reported value is published as filed

`ReportedValue` is the number the manager wrote. The SEC asked for thousands of dollars before 2023
and whole dollars after, and filers on both sides of that change ignore the instruction: in Apple's
December 2019 quarter 85 of 5,365 lines were already in dollars, and scaled as thousands they made
88 percent of the total, an implied 2,577 dollars a share against a 293.65 close.

`ValueScale` is the one reading in a record the SEC does not publish. It is the power of ten that
turns the reported number into dollars, decided per line from the filing's period and from how the
implied price compares with the security's close on the quarter's last trading day: 3 where the
filing states thousands, 0 where it states dollars, and -3 for a line that overstated its value a
thousandfold, which three SPY lines of the March 2023 quarter did, carrying 16 percent of that
quarter's total with them.

It is carried beside the reported number rather than multiplied into it, so what the manager filed
stays readable and this one judgement stays separable from it. `MarketValue` applies it.

### Amendments are published as filed

An amendment is a record like any other, with its `FormType`, `AmendmentType` and `AmendmentNumber`
saying what it is. It does not replace the filing it restates and nothing is netted against
anything. Deciding that a restatement supersedes an earlier figure is a judgement about the data,
and the filer has already declared which kind of amendment it made, so the algorithm can apply it.
Most amendments restate the whole report, which is what `RESTATEMENT` in `AmendmentType` means;
`NEW HOLDINGS` adds to the original.

### A manager can change CIK

A manager whose positions are reported on another manager's filing files a 13F-NT, a notice that
carries no positions and contributes no records. Pershing Square Capital Management (CIK 1336528)
reported its own positions through the March 2026 quarter and has filed a notice since, while
Pershing Square Inc. (CIK 2026053) reports them. Followed by the first CIK alone, the fund appears
to sell everything in one quarter, so follow every CIK the manager has reported under.

### Coverage is partial by design

Holdings are keyed by CUSIP in the source and resolved to a LEAN `Symbol` before publication, so no
identifier from the source is shipped. The resolution runs in four steps, each picking up what the
one before it could not reach:

1. The CUSIP itself, looked up in LEAN's security database. The database repeats some identifiers
   across the listings one company has had, such as the old and the new Alcoa, so every row carrying
   the CUSIP is tried and the one trading under its own ticker on the filing date is kept.
2. The US ISIN, built arithmetically from the same CUSIP. It reaches issuers whose CUSIP column in
   that database is blank.
3. The ticker the SEC's own Form N-PORT filings report for the CUSIP, taken back to a `Symbol`
   through the map files. This step needs no security database at all and it is what reaches the
   foreign domiciled issuers whose identifier is really a CINS, for which a constructed US ISIN is
   wrong by construction. Alphabet is one of them.
4. For an option line, the security the option is written on. A manager reporting options names them
   by the option's own CUSIP, which carries the underlying's six character issuer and issue 90 for
   calls or 95 for puts and appears in no security database. The position is still published as the
   option line it is, with its side in `PutCall`; a line that names no side takes the one its CUSIP
   states. Where the issuer has several funds, as iShares does, every fund's options share one CUSIP,
   and each line goes to the one fund whose quarter-end close its implied price matches, or is dropped.

A security's file holds what managers reported under its CUSIP, which is not always the common
stock: filers put preferred shares, units and convertibles under the common's CUSIP, and
`TitleOfClass` is the only field that says so. `ValueScale` takes the values 3, 0 and -3, so a
filing stated in millions is not brought to dollars.

A fund's reported ticker in step 3 is free text, so that step keeps a match only when the reported
prices do not say otherwise. A group is dropped when three or more of its prices are not the
security's own quarter-end close, in dollars or in thousands, which keeps Centerra Gold, Enerflex,
B2Gold and DeFi Technologies, whose Toronto or fund-reported tickers are CG, EFX, BTO and DEFI, off
Carlyle, Equifax, a John Hancock fund and the Hashdex DEFI ETF. A CUSIP whose issue number carries
letters, as a company's debt does, is rejected outright when that issuer already has stock in the
security database: Apple's 3.45% 2045 bond reached AAPL through a fund administrator's reported
ticker and added the bond's principal amount to Apple's share count as ten thousand shares, and a
bond near par against a stock near the same number agrees with a price test by coincidence. The
iBonds ETFs, whose CUSIPs also carry letters, are themselves in the security database and resolve at
step 1 without ever reaching that rule.

Step 4 is only taken when the issuer has exactly one equity issue in the database. iShares writes
seventy equity issues under 464287 and SPDR eleven under 81369Y, and an option CUSIP there names one
of them without saying which; filing the position under the wrong fund would be worse than not
publishing it. In the week of 3 to 7 August 2026 that step recovered 2,947 of the 6,083 lines
reported on an option CUSIP and left the ambiguous rest out.

The real limit is step 3's own history: N-PORT begins in late 2019, so a security that stopped
trading before then is not reachable through it at any depth and can only be resolved if the first
two steps already found it. Measured over the whole chain, 97.0 percent of the reported lines in the
week of 3 to 7 August 2026 and 95.0 percent of those in the fourth quarter of 2020 resolved to a
security and were published. The product therefore covers most of the reported positions but not
every reported name, and it says so rather than implying full coverage.

## Example Applications

SEC Whales lets you see what large holders actually hold and trade against how crowded a name is.
Examples include the following strategies:

- Screening for crowding by counting the distinct managers reporting each security, and for
  de-crowding by taking the securities whose count fell hardest against the previous quarter.
- Building an ownership change momentum signal from the quarter over quarter move in reported
  shares, and going long the names institutions are accumulating.
- Following one manager through `ManagerCik`, reading what a single fund reported quarter after
  quarter rather than what the market did in aggregate, which is what the demonstration algorithms
  do with Pershing Square.
- Screening out thinly followed names, requiring a minimum number of reporting managers before a
  security is tradeable.
- Reading the reported put and call lines alongside the share positions to see whether managers are
  hedging a name rather than simply owning it.
- Separating sole from defined discretion with `InvestmentDiscretion`, which is the difference
  between a manager's own book and the assets it merely directs.

## Meta

| Field | Value |
| --- | --- |
| name | SEC Whales |
| url | sec-whales |
| vendorName | U.S. Securities and Exchange Commission |
| website | https://www.sec.gov |
| history | May 2013 |
| reach | 8,520 US Equities |
| shortDescription | Ownership filings from the SEC as their holders filed them, starting with Form 13F institutional holdings |
| priceCTA | Free in Cloud |
| delivery | cloud only |

Tags: Financial Market Data

Licensing card:

```html
<p>Free access to SEC Whales in QuantConnect Cloud for use in backtesting or live trading.</p>
<ul>
    <li>Every position reported on Form 13F since 2013, as each manager filed it, across 8,520 US Equities</li>
    <li>Manager, reported quarter, share or principal amount, value, option side, discretion and voting authority per position</li>
    <li>Curated, clean data</li>
</ul>
```
