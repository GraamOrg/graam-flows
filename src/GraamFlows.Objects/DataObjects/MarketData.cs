using System.Xml.Serialization;
using GraamFlows.Objects.TypeEnum;

namespace GraamFlows.Objects.DataObjects;

public class MarketData
{
    public MarketData()
    {
        Quotes = new Dictionary<MarketDataInstEnum, MarketDataQuote>();
    }

    public DateTime MarketRateDate { get; set; }
    public string MarketDataSource { get; set; }
    public double Libor1M { get; set; }
    public double Libor3M { get; set; }
    public double Libor6M { get; set; }
    public double Libor12M { get; set; }
    public double Swap2Y { get; set; }
    public double Swap3Y { get; set; }
    public double Swap4Y { get; set; }
    public double Swap5Y { get; set; }
    public double Swap6Y { get; set; }
    public double Swap7Y { get; set; }
    public double Swap8Y { get; set; }
    public double Swap9Y { get; set; }
    public double Swap10Y { get; set; }
    public double Swap12Y { get; set; }
    public double Swap15Y { get; set; }
    public double Swap20Y { get; set; }
    public double Swap25Y { get; set; }
    public double Swap30Y { get; set; }
    public double Sofr30Avg { get; set; }
    public double Sofr90Avg { get; set; }
    public double Sofr180Avg { get; set; }
    public double SofrIndex { get; set; }

    [XmlIgnore] public Dictionary<MarketDataInstEnum, MarketDataQuote> Quotes { get; }

    public double ValueForIndex(MarketDataInstEnum mdInst)
    {
        switch (mdInst)
        {
            case MarketDataInstEnum.Libor1M:
                return Libor1M;
            case MarketDataInstEnum.Libor3M:
                return Libor3M;
            case MarketDataInstEnum.Libor6M:
                return Libor6M;
            case MarketDataInstEnum.Libor12M:
                return Libor12M;
            case MarketDataInstEnum.Swap2Y:
                return Swap2Y;
            case MarketDataInstEnum.Swap3Y:
                return Swap3Y;
            case MarketDataInstEnum.Swap4Y:
                return Swap4Y;
            case MarketDataInstEnum.Swap5Y:
                return Swap5Y;
            case MarketDataInstEnum.Swap6Y:
                return Swap6Y;
            case MarketDataInstEnum.Swap7Y:
                return Swap7Y;
            case MarketDataInstEnum.Swap8Y:
                return Swap8Y;
            case MarketDataInstEnum.Swap9Y:
                return Swap9Y;
            case MarketDataInstEnum.Swap10Y:
                return Swap10Y;
            case MarketDataInstEnum.Swap12Y:
                return Swap12Y;
            case MarketDataInstEnum.Swap15Y:
                return Swap15Y;
            case MarketDataInstEnum.Swap20Y:
                return Swap20Y;
            case MarketDataInstEnum.Swap25Y:
                return Swap25Y;
            case MarketDataInstEnum.Swap30Y:
                return Swap30Y;
            case MarketDataInstEnum.Sofr30Avg:
                return Sofr30Avg;
            case MarketDataInstEnum.Sofr90Avg:
                return Sofr90Avg;
            case MarketDataInstEnum.Sofr180Avg:
                return Sofr180Avg;
            case MarketDataInstEnum.SofrIndex:
                return SofrIndex;
            default:
                throw new ArgumentException($"{mdInst} is not known!");
        }
    }

    /// <summary>
    ///     The mirror of <see cref="ValueForIndex" />: store the spot rate for one index.
    ///
    ///     It exists so that the set side and the read side are one switch apart in one file
    ///     (graam-flows#102). The API's waterfall path carried its own copy of this mapping,
    ///     and that copy had no case for nine of the nineteen swap tenors and no
    ///     <c>default</c>: an index the caller spelled exactly right parsed, hit no
    ///     unknown-name path, and was then dropped on the floor. Every field here is a bare
    ///     <c>double</c>, so a dropped index is 0.0 and a floating instrument prices at its
    ///     margin alone — a wrong number that looks like an ordinary one.
    ///
    ///     The <c>default</c> throws rather than ignoring: a member added to
    ///     <see cref="MarketDataInstEnum" /> without a case here must fail loudly, not
    ///     silently resolve to zero.
    /// </summary>
    public void SetValueForIndex(MarketDataInstEnum mdInst, double value)
    {
        switch (mdInst)
        {
            case MarketDataInstEnum.Libor1M:
                Libor1M = value;
                break;
            case MarketDataInstEnum.Libor3M:
                Libor3M = value;
                break;
            case MarketDataInstEnum.Libor6M:
                Libor6M = value;
                break;
            case MarketDataInstEnum.Libor12M:
                Libor12M = value;
                break;
            case MarketDataInstEnum.Swap2Y:
                Swap2Y = value;
                break;
            case MarketDataInstEnum.Swap3Y:
                Swap3Y = value;
                break;
            case MarketDataInstEnum.Swap4Y:
                Swap4Y = value;
                break;
            case MarketDataInstEnum.Swap5Y:
                Swap5Y = value;
                break;
            case MarketDataInstEnum.Swap6Y:
                Swap6Y = value;
                break;
            case MarketDataInstEnum.Swap7Y:
                Swap7Y = value;
                break;
            case MarketDataInstEnum.Swap8Y:
                Swap8Y = value;
                break;
            case MarketDataInstEnum.Swap9Y:
                Swap9Y = value;
                break;
            case MarketDataInstEnum.Swap10Y:
                Swap10Y = value;
                break;
            case MarketDataInstEnum.Swap12Y:
                Swap12Y = value;
                break;
            case MarketDataInstEnum.Swap15Y:
                Swap15Y = value;
                break;
            case MarketDataInstEnum.Swap20Y:
                Swap20Y = value;
                break;
            case MarketDataInstEnum.Swap25Y:
                Swap25Y = value;
                break;
            case MarketDataInstEnum.Swap30Y:
                Swap30Y = value;
                break;
            case MarketDataInstEnum.Sofr30Avg:
                Sofr30Avg = value;
                break;
            case MarketDataInstEnum.Sofr90Avg:
                Sofr90Avg = value;
                break;
            case MarketDataInstEnum.Sofr180Avg:
                Sofr180Avg = value;
                break;
            case MarketDataInstEnum.SofrIndex:
                SofrIndex = value;
                break;
            default:
                throw new ArgumentException($"{mdInst} is not known!");
        }
    }
}

public struct MarketDataQuote
{
    public MarketDataQuote(MarketDataTypeEnum mdType, MarketDataInstEnum mdInst, double value, double term)
    {
        MarketDataType = mdType;
        MarketDataInst = mdInst;
        Value = value;
        Term = term;
    }

    public double Value { get; }
    public MarketDataInstEnum MarketDataInst { get; }
    public MarketDataTypeEnum MarketDataType { get; }
    public double Term { get; }
}