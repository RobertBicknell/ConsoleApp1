using System.Text;
using System.Xml;
using Csv2Xml.Design.Matching;
using Csv2Xml.Runtime.Mapping;

namespace Csv2Xml.Tests;

public class ConfigJsonTests
{
    static MappingSpec OrdersSpec()
    {
        return TestKit.Build(TestKit.Kind("Orders"), "order_id,customer_id,order_date,sku,quantity,unit_price".Split(',')) with
        {
            GroupKeys = new Dictionary<string, IReadOnlyList<string>> { ["Orders/Order"] = ["Orders/Order/@orderId"] },
        };
    }

    static string Transform(MappingSpec spec)
    {
        var sb = new StringBuilder();
        using (var xw = XmlWriter.Create(sb))
            TestKit.Transform(spec, new StringReader("order_id,customer_id,order_date,sku,quantity,unit_price\n10,1,2024-01-02,A,2,1.50\n10,1,2024-01-02,B,1,9.99\n"), xw);
        return sb.ToString();
    }

    [Fact]
    public void Mapping_round_trips_via_string_and_bytes_and_produces_identical_output()
    {
        var spec = OrdersSpec();
        var fromString = ConfigJson.MappingFromJson(ConfigJson.ToJson(spec));
        var fromBytes = ConfigJson.MappingFromBytes(ConfigJson.ToBytes(spec));

        Assert.Equal(ConfigJson.ToJson(spec), ConfigJson.ToJson(fromString));
        Assert.Equal(ConfigJson.ToJson(spec), ConfigJson.ToJson(fromBytes));
        Assert.Equal(Transform(spec), Transform(fromBytes));
    }

    [Fact]
    public void Job_round_trips()
    {
        var job = SpecBuilder.BuildJob("Weekly", TestKit.Doc, [OrdersSpec()]) with { ErrorPolicy = Runtime.Writing.ErrorPolicy.SkipRow };
        var back = ConfigJson.JobFromBytes(ConfigJson.ToBytes(job));
        Assert.Equal(ConfigJson.ToJson(job), ConfigJson.ToJson(back));
        Assert.Equal(Runtime.Writing.ErrorPolicy.SkipRow, back.ErrorPolicy);
        Assert.Contains("\"SkipRow\"", ConfigJson.ToJson(job));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{\"format\":\"csv2xml.job\",\"version\":1,\"content\":{}}")]
    [InlineData("{\"format\":\"csv2xml.mapping\",\"version\":99,\"content\":{}}")]
    public void Rejects_wrong_format_or_version(string json)
    {
        Assert.Throws<InvalidDataException>(() => ConfigJson.MappingFromJson(json));
    }
}
