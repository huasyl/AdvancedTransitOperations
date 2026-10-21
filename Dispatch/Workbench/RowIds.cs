using System;
using System.Text;

namespace RapidTransitMod.Dispatch.Workbench
{
    internal static class RowIds
    {
        // FixedString128Bytes reserves three bytes for its length and terminator.
        internal static string ForApply(string id)
        {
            if (string.IsNullOrEmpty(id) || Encoding.UTF8.GetByteCount(id) <= 125)
                return id;

            uint first = 0xdeadbeef;
            uint second = 0x41c6ce57;
            unchecked
            {
                foreach (char character in id)
                {
                    first = (first ^ character) * 2654435761;
                    second = (second ^ character) * 1597334677;
                }
                first = ((first ^ (first >> 16)) * 2246822507) ^ ((second ^ (second >> 13)) * 3266489909);
                second = ((second ^ (second >> 16)) * 2246822507) ^ ((first ^ (first >> 13)) * 3266489909);
            }

            ulong hash = ((ulong)(second & 0x1fffff) << 32) | first;
            const string digits = "0123456789abcdefghijklmnopqrstuvwxyz";
            char[] encoded = new char[12];
            encoded[0] = 'r';
            for (int index = 11; index > 0; index--)
            {
                encoded[index] = digits[(int)(hash % 36)];
                hash /= 36;
            }
            return new string(encoded);
        }
    }
}
