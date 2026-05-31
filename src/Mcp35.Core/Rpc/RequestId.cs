using System;
using System.Globalization;

namespace Mcp35.Core.Rpc
{
    /// <summary>
    /// JSON-RPC id is <c>string | number | null</c>. Modelled as a small value type so
    /// correlation keys are well-typed, hash/equate correctly (they back the pending-request
    /// table), and round-trip in their original kind. See mcp35-core-spec.md section 2.
    /// </summary>
    public struct RequestId : IEquatable<RequestId>
    {
        private enum Kind { Null, Long, String }

        private readonly Kind _kind;
        private readonly long _num;
        private readonly string _str;

        private RequestId(Kind kind, long num, string str)
        {
            _kind = kind;
            _num = num;
            _str = str;
        }

        /// <summary>The JSON-RPC null id (default(RequestId) is also null).</summary>
        public static readonly RequestId Null = new RequestId(Kind.Null, 0, null);

        public static RequestId FromLong(long n)
        {
            return new RequestId(Kind.Long, n, null);
        }

        public static RequestId FromString(string s)
        {
            if (s == null) return Null;
            return new RequestId(Kind.String, 0, s);
        }

        public bool IsNull { get { return _kind == Kind.Null; } }
        public bool IsLong { get { return _kind == Kind.Long; } }
        public bool IsString { get { return _kind == Kind.String; } }

        public long AsLong { get { return _num; } }
        public string AsString { get { return _str; } }

        public bool Equals(RequestId other)
        {
            if (_kind != other._kind) return false;
            switch (_kind)
            {
                case Kind.Long: return _num == other._num;
                case Kind.String: return string.Equals(_str, other._str, StringComparison.Ordinal);
                default: return true; // both Null
            }
        }

        public override bool Equals(object obj)
        {
            return obj is RequestId && Equals((RequestId)obj);
        }

        public override int GetHashCode()
        {
            switch (_kind)
            {
                case Kind.Long: return _num.GetHashCode();
                case Kind.String: return _str == null ? 0 : _str.GetHashCode();
                default: return 0;
            }
        }

        public override string ToString()
        {
            switch (_kind)
            {
                case Kind.Long: return _num.ToString(CultureInfo.InvariantCulture);
                case Kind.String: return _str;
                default: return "(null)";
            }
        }
    }
}
