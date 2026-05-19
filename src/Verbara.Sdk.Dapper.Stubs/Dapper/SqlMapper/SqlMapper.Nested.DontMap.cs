namespace Dapper;

public static partial class SqlMapper
{
    /// <summary>
    /// AOT-clean stub of <c>SqlMapper.DontMap</c>. Real Dapper exposes this as a plain class
    /// (NOT an Attribute subclass — verified via reflection on Dapper 2.1.72). The spec text
    /// labeling it a "marker attribute" is inaccurate; the actual public surface is a plain
    /// class with a public default constructor and System.Object as its base.
    /// </summary>
    public class DontMap
    {
        public DontMap() { }
    }
}
