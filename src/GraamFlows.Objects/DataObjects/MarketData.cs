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