namespace SqlLogReader.Log;

/// <summary>Numeric operation and context codes stored in log records, with their fn_dblog names.</summary>
public static class LogOperationCodes
{
    private static readonly Dictionary<byte, string> Operations = new()
    {
        [0x01] = "LOP_FORMAT_PAGE",
        [0x02] = "LOP_INSERT_ROWS",
        [0x03] = "LOP_DELETE_ROWS",
        [0x04] = "LOP_MODIFY_ROW",
        [0x05] = "LOP_MODIFY_HEADER",
        [0x06] = "LOP_MODIFY_COLUMNS",
        [0x07] = "LOP_SET_BITS",
        [0x0A] = "LOP_SET_FREE_SPACE",
        [0x0B] = "LOP_DELETE_SPLIT",
        [0x0D] = "LOP_EXPUNGE_ROWS",
        [0x10] = "LOP_FILE_HDR_MODIFY",
        [0x11] = "LOP_CLEAR_GAM_BITS",
        [0x12] = "LOP_COUNT_DELTA",
        [0x13] = "LOP_ROOT_CHANGE",
        [0x16] = "LOP_INSYSXACT",
        [0x80] = "LOP_BEGIN_XACT",
        [0x81] = "LOP_COMMIT_XACT",
        [0x82] = "LOP_ABORT_XACT",
        [0x83] = "LOP_PREP_XACT",
        [0x84] = "LOP_MARK_SAVEPOINT",
        [0x85] = "LOP_FORGET_XACT",
        [0x86] = "LOP_CREATE_FILE",
        [0x89] = "LOP_HOBT_DDL",
        [0x8C] = "LOP_HOBT_DELTA",
        [0x8D] = "LOP_LOCK_XACT",
        [0x8F] = "LOP_MIGRATE_LOCKS",
        [0x96] = "LOP_BEGIN_CKPT",
        [0x98] = "LOP_XACT_CKPT",
        [0x99] = "LOP_END_CKPT",
        [0xB0] = "LOP_CREATE_ALLOCCHAIN",
        [0xD3] = "LOP_SHRINK_NOOP",
        [0xD9] = "LOP_CREATE_PHYSICAL_FILE",
        [0xDD] = "LOP_INVALIDATE_CACHE",
    };

    private static readonly Dictionary<byte, string> Contexts = new()
    {
        [0x00] = "LCX_NULL",
        [0x01] = "LCX_HEAP",
        [0x02] = "LCX_CLUSTERED",
        [0x03] = "LCX_INDEX_LEAF",
        [0x04] = "LCX_INDEX_INTERIOR",
        [0x05] = "LCX_TEXT_MIX",
        [0x07] = "LCX_DIAGNOSTICS",
        [0x08] = "LCX_GAM",
        [0x09] = "LCX_SGAM",
        [0x0A] = "LCX_IAM",
        [0x0B] = "LCX_PFS",
        [0x0D] = "LCX_OBJECT_ID",
        [0x11] = "LCX_FILE_HEADER",
        [0x12] = "LCX_SCHEMA_VERSION",
        [0x13] = "LCX_MARK_AS_GHOST",
        [0x14] = "LCX_BOOT_PAGE",
        [0x17] = "LCX_BOOT_PAGE_CKPT",
        [0x18] = "LCX_DIFF_MAP",
    };

    public static string OperationName(byte code) => Operations.TryGetValue(code, out var name) ? name : $"LOP_0x{code:X2}";

    public static string ContextName(byte code) => Contexts.TryGetValue(code, out var name) ? name : $"LCX_0x{code:X2}";

    public static bool IsRowOperation(byte code) => code is 0x02 or 0x03 or 0x04 or 0x06;
}
