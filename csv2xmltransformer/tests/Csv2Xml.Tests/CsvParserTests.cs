using Csv2Xml.Runtime.Csv;

namespace Csv2Xml.Tests;

public class CsvParserTests
{
    static List<string[]> Parse(string s, char d = ',') => CsvParser.Read(new StringReader(s), d).ToList();

    [Fact] public void Simple() => Assert.Equal([["a", "b"], ["1", "2"]], Parse("a,b\n1,2\n"));
    [Fact] public void No_trailing_newline() => Assert.Equal([["a", "b"], ["1", "2"]], Parse("a,b\n1,2"));
    [Fact] public void Crlf_and_cr() => Assert.Equal([["a"], ["b"], ["c"]], Parse("a\r\nb\rc"));
    [Fact] public void Quoted_delimiter_newline_and_escaped_quote() =>
        Assert.Equal([["x,y", "line1\nline2", "say \"hi\""]], Parse("\"x,y\",\"line1\nline2\",\"say \"\"hi\"\"\""));
    [Fact] public void Empty_fields_preserved() => Assert.Equal([["", "", ""], ["a", "", ""]], Parse(",,\na,,"));
    [Fact] public void Blank_lines_skipped() => Assert.Equal([["a"], ["b"]], Parse("a\n\n\nb\n"));
    [Fact] public void Custom_delimiter() => Assert.Equal([["a", "b,c"]], Parse("a;b,c", ';'));
    [Fact] public void Quoted_empty_field_is_a_row() => Assert.Equal([[""]], Parse("\"\"\n"));
    [Fact] public void Mid_field_quote_is_literal() => Assert.Equal([["5\" pipe"]], Parse("5\" pipe"));
    [Fact] public void Quote_at_buffer_boundary()
    {
        var big = new string('x', 8191) + "\"\"";
        Assert.Equal(8192 + 0, Parse("\"" + big + "\"")[0][0].Length);
    }
    [Fact] public void Streams_lazily()
    {
        var reader = new CountingReader();
        var first = CsvParser.Read(reader).First();
        Assert.Equal(["a"], first);
        Assert.True(reader.Reads <= 2);
    }

    sealed class CountingReader : TextReader
    {
        public int Reads;
        public override int Read(char[] buffer, int index, int count)
        {
            Reads++;
            var s = "a\n"; s.CopyTo(0, buffer, index, s.Length); return s.Length;
        }
    }
}
