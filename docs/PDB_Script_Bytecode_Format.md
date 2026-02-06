# PDB Script Bytecode Format — Fallout: New Vegas (Xbox 360)

All data sourced from PDB symbol dumps and game executables. Verified 2026-02-06.

## Source Files

| File                | Path                                                                     | Notes                                                    |
| ------------------- | ------------------------------------------------------------------------ | -------------------------------------------------------- |
| PDB types (debug)   | `Sample/PDB/Fallout_Debug/types_full.txt`                                | Primary source for struct/enum definitions               |
| PDB globals (debug) | `Sample/PDB/Fallout_Debug/globals.txt`                                   | Global variable addresses and function offsets           |
| Final build exe     | `Sample/Fallout New Vegas (Aug 22, 2010)/Diskuild_1.0.0.252/Fallout.exe` | Xbox 360 PowerPC PE (machine 0x1F2), XEX base 0x82000000 |
| Prototype exe       | `Sample/Fallout New Vegas (July 21, 2010)/FalloutNV/Fallout.exe`         | Earlier prototype (2 param differences vs final)         |

### Executable Layout (Aug 22, 2010 Final Build)

| Section | VA         | Raw Offset | Key Data                                               |
| ------- | ---------- | ---------- | ------------------------------------------------------ |
| .data   | 0x00FE0400 | 0x00FD1800 | scriptConsole at +0x27008, scriptFunctions at +0x29038 |
| .rdata  | 0x00000600 | 0x00000600 | Function name strings, parameter name strings          |

### Key Global Addresses (from globals.txt)

| Global          | Address         | Type                               | Count                                      |
| --------------- | --------------- | ---------------------------------- | ------------------------------------------ |
| scriptConsole   | [0007:00027058] | SCRIPT_FUNCTION[]                  | 205 entries (opcodes 0x100-0x1CC)          |
| scriptFunctions | [0007:00029088] | SCRIPT_FUNCTION[625] (25000 bytes) | 624 game functions (opcodes 0x1000-0x126F) |

### Key Function Addresses (for disassembly verification)

| Function                          | Section:Offset | Purpose                                          |
| --------------------------------- | -------------- | ------------------------------------------------ |
| ScriptRunner::GetNextLine         | [0006:0x20D8]  | Bytecode reader — defines token format           |
| ScriptRunner::EvaluateLine        | [0006:0x227C]  | Expression evaluator — defines RPN tokens        |
| ScriptRunner::EndianNextLine      | [0006:0x1FDC]  | Endian swap for one bytecode line                |
| ScriptRunner::Endian              | [0006:0x2494]  | Endian swap for entire script                    |
| ScriptCompiler::CompileLine       | [0008:0x3508]  | Compiles one source line — defines output format |
| ScriptCompiler::GetNextExpression | [0008:0x33F4]  | Expression compiler — defines token encoding     |

---

## Key Classes

### ScriptCompiler (Size=1, 30 members)

- Static utility class for compiling script source text to bytecode.
- Source: types_full.txt line 824730 (field list 0x22dd3)

| Method                                    | Visibility | Type       | Notes                                                                 |
| ----------------------------------------- | ---------- | ---------- | --------------------------------------------------------------------- |
| ScriptCompiler / ~ScriptCompiler          | public     | ctor/dtor  |                                                                       |
| CompilePartialScript                      | public     | instance   |                                                                       |
| CompileFullScript                         | public     | instance   |                                                                       |
| **StandardCompile**                       | public     | **static** | `bool(Script*, char*, char*, bool)` — main entry point                |
| MessageBoxCompile (x2)                    | public     | static     | overloaded                                                            |
| MessageCompile                            | public     | static     | same sig as StandardCompile                                           |
| OldMessageCompile                         | public     | static     | same sig as StandardCompile                                           |
| IsValidVariableName                       | public     | static     | `bool(char*)`                                                         |
| ScriptError                               | public     | static     | `void(Script*, char*, ...)`                                           |
| **GetFunctionDef**                        | public     | static     | Returns `SCRIPT_FUNCTION*`, takes 1 param (opcode/name)               |
| Compile                                   | protected  | instance   | `bool(Script*, Script*)`                                              |
| **CompileLine**                           | protected  | instance   | `bool(Script*, Script*, SCRIPT_LINE*)`                                |
| **GetNextExpression**                     | protected  | instance   | Returns `SCRIPT_OUTPUT` enum value                                    |
| GetNextLine                               | protected  | static     | `int(Script*, SCRIPT_LINE*)`                                          |
| VerifyBlocks                              | protected  | instance   |                                                                       |
| CountBlockLines / CountIfLines            | protected  | instance   |                                                                       |
| CountVariablesNeeded / CountButtonsNeeded | protected  | instance   |                                                                       |
| **ReplaceVariablesAndFunctions**          | protected  | instance   | `uint(ScriptCompileData*, const SCRIPT_PARAMETER_DEF*, char*, char*)` |
| **VerifyParameters**                      | protected  | instance   | `void(SCRIPT_FUNCTION*, int)`                                         |
| GetFunction                               | protected  | static     |                                                                       |
| GetAnimGroup                              | protected  | instance   | Returns `ANIM_GROUP_LIST` enum (0xBB1E)                               |
| GetNextWord                               | protected  | static     |                                                                       |
| SkipWhiteSpace                            | protected  | static     |                                                                       |
| CheckQuotes / CheckParentheses            | protected  | static     |                                                                       |

### ScriptRunner (Size=164, 26 members)

- Runtime execution engine for compiled script bytecode.
- Source: types_full.txt line 644788 (field list 0x1b40c)

| Method             | Signature                                                                           |
| ------------------ | ----------------------------------------------------------------------------------- |
| **GetNextLine**    | `bool(Script*, SCRIPT_OUTPUT&, uint&, int&, TESObjectREFR**, bool)`                 |
| **EndianNextLine** | `bool(Script*, SCRIPT_OUTPUT&, uint&, int&, TESObjectREFR**)`                       |
| **EvaluateLine**   | `bool(Script*, SCRIPT_OUTPUT, TESObjectREFR*, int&, uint, uint&, uint, bool, bool)` |
| **Endian**         | `void(Script*)` — endian-swaps entire compiled script                               |
| Run                | Main execution loop                                                                 |

#### ScriptRunner Fields (Size=164)

| Offset | Type            | Name                         |
| ------ | --------------- | ---------------------------- |
| 0      | TESObjectREFR\* | m_currentContainer           |
| 4      | type 0xCEF0     | m_currentObject              |
| 8      | ScriptLocals\*  | m_currentVars                |
| 12     | type 0xD79A     | m_currentInfo                |
| 16     | type 0xD5C1     | m_global                     |
| 20     | Script\*        | m_scriptRunning              |
| 24     | SCRIPT_ERROR    | m_error                      |
| 28     | type 0xBB88     | m_expError                   |
| 32     | int             | m_ifNested                   |
| 36     | int[10]         | m_ifFlags (40 bytes)         |
| 76     | int             | m_whileNested                |
| 80     | int[10]         | m_whileFlags (40 bytes)      |
| 120    | int[10]         | m_whileOffset (40 bytes)     |
| 160    | bool            | m_bScriptErrorFound          |
| 161    | bool            | m_bStopObjectScriptExecution |

### Script (Size=84, 1195 members)

- The script object itself. Contains compiled data, variables, references.
- Source: types_full.txt line 760964 (field list 0x203ac, 38930 bytes)

| Key Method                    | Signature                                                                                                                         | Notes                             |
| ----------------------------- | --------------------------------------------------------------------------------------------------------------------------------- | --------------------------------- |
| **PutNumericIDInDouble**      | `static void(const uint& id, double& result)`                                                                                     | Packs uint32 into IEEE 754 double |
| **GetNumericIDFromDouble**    | `static void(uint& id, const double& value)`                                                                                      | Extracts uint32 from double       |
| **ParseParameters**           | `static bool(const SCRIPT_PARAMETER*, const char*, TESObjectREFR*, TESObjectREFR*, Script*, ScriptLocals*, double&, uint&, ...)`  | Varargs                           |
| **GetValue**                  | `static bool(const SCRIPT_PARAMETER*, const char*, TESObjectREFR*, TESObjectREFR*, Script*, ScriptLocals*, double&, uint&, bool)` |                                   |
| GetHeader                     | instance                                                                                                                          | Returns SCRIPT_HEADER\*           |
| GetText / GetTextLength       | instance                                                                                                                          | Source text access                |
| GetCompiledData               | instance                                                                                                                          | Returns compiled bytecode pointer |
| SetCompileData                | instance                                                                                                                          | Sets compiled data                |
| GetVariableList               | instance                                                                                                                          | Returns variable list             |
| GetReferencedObject           | instance                                                                                                                          | Gets SCRO by index                |
| GetReferencedObjectList       | instance                                                                                                                          | Returns SCRO list                 |
| SetOwnerQuest / GetOwnerQuest | instance                                                                                                                          | Quest association                 |

### ScriptCompileData (Size=88, 29 members)

- Compiler context, holds state during compilation.
- Source: types_full.txt line 547864 (field list 0x17123, struct 0x17124)

| Offset | Type              | Name               | Notes                                                                   |
| ------ | ----------------- | ------------------ | ----------------------------------------------------------------------- |
| 0      | char\*            | input              | Source text pointer                                                     |
| 4      | uint              | uiInputOffset      | Current read position in source                                         |
| 8      | COMPILER_NAME     | eCompilerIndex     | Which compiler (DEFAULT=0, SYSTEM_WINDOW=1, DIALOGUE=2)                 |
| 12     | BSStringT\<char\> | cScriptName        | Script name (8 bytes)                                                   |
| 20     | SCRIPT_ERROR      | eLastError         | Last compilation error                                                  |
| 24     | bool              | bIsPartialScript   |                                                                         |
| 28     | uint              | uiLastLineNumber   |                                                                         |
| 32     | char\*            | **pOutput**        | Output bytecode buffer                                                  |
| 36     | uint              | **uiOutputOffset** | Current write position in output                                        |
| 40     | SCRIPT_HEADER     | header             | Embedded 20-byte header (variableCount, refObjectCount, dataSize, etc.) |
| 60     | BSSimpleList      | listVariables      | Variable definitions                                                    |
| 68     | BSSimpleList      | listRefObjects     | Referenced objects                                                      |
| 76     | Script\*          | pCurrentScript     |                                                                         |
| 80     | BSSimpleList      | listLines          | SCRIPT_LINE list (private)                                              |

---

## Key Structures

### SCRIPT_HEADER (Size=20, 8 members)

- The compiled script header, matches SCHR subrecord in ESM.
- Source: types_full.txt line 540736 (field list 0x16c2a, struct 0x16c2b)

| Offset | Type   | Name                 | Notes                             |
| ------ | ------ | -------------------- | --------------------------------- |
| 0      | uint32 | variableCount        | Number of script variables        |
| 4      | uint32 | refObjectCount       | Number of SCRO referenced objects |
| 8      | uint32 | dataSize             | Size of compiled bytecode (SCDA)  |
| 12     | uint32 | m_uiLastID           | Last variable ID assigned         |
| 16     | bool   | bIsQuestScript       |                                   |
| 17     | bool   | bIsMagicEffectScript |                                   |
| 18     | bool   | bIsCompiled          |                                   |

Has `Endian()` method for byte-swapping.

### SCRIPT_FUNCTION (Size=40, 12 members)

- Defines a single script-callable function (opcode definition).
- Source: types_full.txt line 750229 (field list 0x1fc5e, struct 0x1fc5f)

| Offset | Type               | Name                 | Notes                             |
| ------ | ------------------ | -------------------- | --------------------------------- |
| 0      | char\*             | pFunctionName        | Full name (e.g., "GetActorValue") |
| 4      | char\*             | pShortName           | Abbreviated (e.g., "GetAV")       |
| 8      | SCRIPT_OUTPUT      | eOutput              | Opcode enum value                 |
| 12     | char\*             | pHelpString          | Description text                  |
| 16     | bool               | bReferenceFunction   | Whether operates on a reference   |
| 18     | ushort             | **sParamCount**      | Number of parameters              |
| 20     | SCRIPT_PARAMETER\* | **pParameters**      | Array of parameter definitions    |
| 24     | pExecuteFunction   | **Execute callback** | See signature below               |
| 28     | pCompileFunction   | **Compile callback** | See signature below               |
| 32     | pConditionFunction | Condition callback   |                                   |
| 36     | bool               | bEditorFilter        |                                   |
| 37     | bool               | bInvalidatesCellList |                                   |

#### pExecuteFunction Signature

```c
bool pExecuteFunction(
    const SCRIPT_PARAMETER* params,     // Parameter type definitions
    const char* compiledParamData,      // RAW compiled parameter bytes
    TESObjectREFR* callingRef,          // Object running the script
    TESObjectREFR* containingRef,       // Container/parent object
    Script* script,                      // The script
    ScriptLocals* locals,               // Script local variables
    double& result,                      // Output result value
    uint& opcodeOffset                   // Bytecode offset
);
```

#### pCompileFunction Signature

```c
bool pCompileFunction(
    const ushort opcode,                // The function opcode
    const SCRIPT_PARAMETER* params,     // Parameter type definitions
    SCRIPT_LINE* outputLine,            // Output compiled line buffer
    ScriptCompileData* compileData      // Compiler context
);
```

### SCRIPT_PARAMETER (Size=12, 3 members)

- Defines one parameter of a script function.
- Source: types_full.txt line 718646 (field list 0x1e730, struct 0x1e731)

| Offset | Type              | Name           | Notes                         |
| ------ | ----------------- | -------------- | ----------------------------- |
| 0      | char\*            | pParamName     | Parameter name string         |
| 4      | SCRIPT_PARAM_TYPE | **eParamType** | Type enum value               |
| 8      | bool              | **bOptional**  | Whether parameter is optional |

### SCRIPT_PARAMETER_DEF (Size=8, 3 members)

- Compiled-form parameter definition.
- Source: types_full.txt line 1055729 (field list 0x2cdfa, struct 0x2cdfb)
- Note: Found in a static array of 560 bytes = 70 entries.

| Offset | Type              | Name                  | Notes                                      |
| ------ | ----------------- | --------------------- | ------------------------------------------ |
| 0      | SCRIPT_PARAM_TYPE | **eParamType**        | Parameter type enum                        |
| 4      | bool              | **bCanBeVariable**    | If true, value may be a variable reference |
| 5      | bool              | **bReferencedObject** | If true, value is a FormID/SCRO reference  |

### SCRIPT_LINE (Size=1052, 10 members)

- Runtime representation of one compiled script line.
- Source: types_full.txt line 699836 (field list 0x1d9f7, struct 0x1d9f8)

| Offset | Type          | Name             | Notes                            |
| ------ | ------------- | ---------------- | -------------------------------- |
| 0      | uint          | uiLineNumber     | Source line number               |
| 4      | char[512]     | pLine            | Source text for this line        |
| 516    | uint          | uiSize           | Source text length               |
| 520    | uint          | uiOffset         | Offset in compiled bytecode      |
| 524    | char[512]     | **pOutput**      | **Compiled bytes for this line** |
| 1036   | uint          | **uiOutputSize** | Size of compiled output          |
| 1040   | SCRIPT_OUTPUT | sExpression      | The opcode for this line         |
| 1044   | uint          | uiRefObjectIndex | SCRO reference index             |
| 1048   | SCRIPT_ERROR  | eScriptError     | Error status                     |

---

## Key Enums

### SCRIPT_OUTPUT (type 0xBB20)

- 855 members. All `FUNCTION_*` constants mapping to opcode numbers.
- Source: types_full.txt line 284255 (LF_ENUM), field list 0xBB1F at line 283398

### SCRIPT_PARAM_TYPE (type 0xBB3A, 71 values)

- Complete parameter type enum (field list 0xBB39 at PDB line 284537, LF_ENUM at line 284610).

| Value | Name                               | Compiled Data Size (hypothesis) |
| ----- | ---------------------------------- | ------------------------------- |
| 0     | SCRIPT_PARAM_CHAR                  | Variable (string)               |
| 1     | SCRIPT_PARAM_INT                   | 4 bytes (int32)                 |
| 2     | SCRIPT_PARAM_FLOAT                 | 8 bytes (double)                |
| 3     | SCRIPT_PARAM_INVENTORY_OBJECT      | 2 bytes (SCRO ref index)        |
| 4     | SCRIPT_PARAM_OBJECTREF             | 2 bytes (SCRO ref index)        |
| 5     | SCRIPT_PARAM_ACTOR_VALUE           | 2 bytes (AV index)              |
| 6     | SCRIPT_PARAM_ACTOR                 | 2 bytes (SCRO ref index)        |
| 7     | SCRIPT_PARAM_SPELL_ITEM            | 2 bytes (SCRO ref index)        |
| 8     | SCRIPT_PARAM_AXIS                  | 2 bytes (axis code)             |
| 9     | SCRIPT_PARAM_CELL                  | 2 bytes (SCRO ref index)        |
| 10    | SCRIPT_PARAM_ANIM_GROUP            | 2 bytes (anim group code)       |
| 11    | SCRIPT_PARAM_MAGIC_ITEM            | 2 bytes (SCRO ref index)        |
| 12    | SCRIPT_PARAM_SOUND                 | 2 bytes (SCRO ref index)        |
| 13    | SCRIPT_PARAM_TOPIC                 | 2 bytes (SCRO ref index)        |
| 14    | SCRIPT_PARAM_QUEST                 | 2 bytes (SCRO ref index)        |
| 15    | SCRIPT_PARAM_RACE                  | 2 bytes (SCRO ref index)        |
| 16    | SCRIPT_PARAM_CLASS                 | 2 bytes (SCRO ref index)        |
| 17    | SCRIPT_PARAM_FACTION               | 2 bytes (SCRO ref index)        |
| 18    | SCRIPT_PARAM_SEX                   | 2 bytes (sex code)              |
| 19    | SCRIPT_PARAM_GLOBAL                | 2 bytes (SCRO ref index)        |
| 20    | SCRIPT_PARAM_FURNITURE_OR_FORMLIST | 2 bytes (SCRO ref index)        |
| 21    | SCRIPT_PARAM_OBJECT                | 2 bytes (SCRO ref index)        |
| 22    | SCRIPT_PARAM_SCRIPT_VAR            | Variable (script var ref)       |
| 23    | SCRIPT_PARAM_STAGE                 | 2 bytes (stage number)          |
| 24    | SCRIPT_PARAM_MAP_MARKER            | 2 bytes (SCRO ref index)        |
| 25    | SCRIPT_PARAM_ACTOR_BASE            | 2 bytes (SCRO ref index)        |
| 26    | SCRIPT_PARAM_CONTAINER_REF         | 2 bytes (SCRO ref index)        |
| 27    | SCRIPT_PARAM_WORLD                 | 2 bytes (SCRO ref index)        |
| 28    | SCRIPT_PARAM_CRIME_TYPE            | 2 bytes (crime type code)       |
| 29    | SCRIPT_PARAM_PACKAGE               | 2 bytes (SCRO ref index)        |
| 30    | SCRIPT_PARAM_COMBAT_STYLE          | 2 bytes (SCRO ref index)        |
| 31    | SCRIPT_PARAM_MAGIC_EFFECT          | 2 bytes (SCRO ref index)        |
| 32    | SCRIPT_PARAM_FORM_TYPE             | 2 bytes (form type code)        |
| 33    | SCRIPT_PARAM_WEATHER               | 2 bytes (SCRO ref index)        |
| 34    | SCRIPT_PARAM_NPC                   | 2 bytes (SCRO ref index)        |
| 35    | SCRIPT_PARAM_OWNER                 | 2 bytes (SCRO ref index)        |
| 36    | SCRIPT_PARAM_SHADER_EFFECT         | 2 bytes (SCRO ref index)        |
| 37    | SCRIPT_PARAM_FORMLIST              | 2 bytes (SCRO ref index)        |
| 38    | SCRIPT_PARAM_MENUICON              | 2 bytes (SCRO ref index)        |
| 39    | SCRIPT_PARAM_PERK                  | 2 bytes (SCRO ref index)        |
| 40    | SCRIPT_PARAM_NOTE                  | 2 bytes (SCRO ref index)        |
| 41    | SCRIPT_PARAM_MISC_STAT             | 2 bytes (stat code)             |
| 42    | SCRIPT_PARAM_IMAGESPACEMOD         | 2 bytes (SCRO ref index)        |
| 43    | SCRIPT_PARAM_IMAGESPACE            | 2 bytes (SCRO ref index)        |
| 44    | SCRIPT_PARAM_VATS_VALUE            | 2 bytes (VATS value code)       |
| 45    | SCRIPT_PARAM_VATS_VALUE_DATA       | Variable                        |
| 46    | SCRIPT_PARAM_VOICE_TYPE            | 2 bytes (SCRO ref index)        |
| 47    | SCRIPT_PARAM_ENCOUNTERZONE         | 2 bytes (SCRO ref index)        |
| 48    | SCRIPT_PARAM_IDLE_FORM             | 2 bytes (SCRO ref index)        |
| 49    | SCRIPT_PARAM_MESSAGE               | 2 bytes (SCRO ref index)        |
| 50    | SCRIPT_PARAM_INVOBJECT_OR_FORMLIST | 2 bytes (SCRO ref index)        |
| 51    | SCRIPT_PARAM_ALIGNMENT             | 2 bytes (alignment code)        |
| 52    | SCRIPT_PARAM_EQUIPTYPE             | 2 bytes (equip type code)       |
| 53    | SCRIPT_PARAM_OBJECT_OR_FORMLIST    | 2 bytes (SCRO ref index)        |
| 54    | SCRIPT_PARAM_MUSIC                 | 2 bytes (SCRO ref index)        |
| 55    | SCRIPT_PARAM_CRITSTAGE             | 2 bytes (crit stage code)       |
| 56    | SCRIPT_PARAM_NPC_OR_LEVCHAR        | 2 bytes (SCRO ref index)        |
| 57    | SCRIPT_PARAM_CREA_OR_LEVCREA       | 2 bytes (SCRO ref index)        |
| 58    | SCRIPT_PARAM_LEVCHAR               | 2 bytes (SCRO ref index)        |
| 59    | SCRIPT_PARAM_LEVCREA               | 2 bytes (SCRO ref index)        |
| 60    | SCRIPT_PARAM_LEVITEM               | 2 bytes (SCRO ref index)        |
| 61    | SCRIPT_PARAM_FORM                  | 2 bytes (SCRO ref index)        |
| 62    | SCRIPT_PARAM_REPUTATION            | 2 bytes (SCRO ref index)        |
| 63    | SCRIPT_PARAM_CASINO                | 2 bytes (SCRO ref index)        |
| 64    | SCRIPT_PARAM_CASINOCHIP            | 2 bytes (SCRO ref index)        |
| 65    | SCRIPT_PARAM_CHALLENGE             | 2 bytes (SCRO ref index)        |
| 66    | SCRIPT_PARAM_CARAVANMONEY          | 2 bytes (SCRO ref index)        |
| 67    | SCRIPT_PARAM_CARAVANCARD           | 2 bytes (SCRO ref index)        |
| 68    | SCRIPT_PARAM_CARAVANDECK           | 2 bytes (SCRO ref index)        |
| 69    | SCRIPT_PARAM_REGION                | 2 bytes (SCRO ref index)        |
| 70    | SCRIPT_PARAM_COUNT                 | (sentinel)                      |

### COMPILER_NAME (type 0xBB22)

| Value | Name                   |
| ----- | ---------------------- |
| 0     | DEFAULT_COMPILER       |
| 1     | SYSTEM_WINDOW_COMPILER |
| 2     | DIALOGUE_COMPILER      |
| 3     | COMPILER_NAME_COUNT    |

### SCRIPT_ERROR (type 0xBB3E)

| Value | Name                             |
| ----- | -------------------------------- |
| 0     | ERROR_SCRIPT_NOERROR             |
| 1     | ERROR_SCRIPT_SCRIPT_NAME_MISSING |
| 2     | ERROR_SCRIPT_SYNTAX              |
| 3     | ERROR_SCRIPT_BADVARIABLENAME     |
| 4     | ERROR_SCRIPT_UNKNOWN_VARIABLE    |
| 5     | ERROR_SCRIPT_NOCOMMANDS          |
| 6     | ERROR_SCRIPT_EXPRESSION          |
| 7     | ERROR_SCRIPT_MISSING_SETVARALIAS |
| 8     | ERROR_SCRIPT_WSTACKOVERFLOW      |
| 9     | ERROR_SCRIPT_WSTACKUNDERFLOW     |
| 10    | ERROR_SCRIPT_IFSTACKOVERFLOW     |
| 11    | ERROR_SCRIPT_IFSTACKUNDERFLOW    |
| 12    | ERROR_SCRIPT_OUTOFMEMORY         |
| 13    | ERROR_SCRIPT_UNHANDLEDCOMMAND    |
| 14    | ERROR_SCRIPT_FILE_TYPE           |
| 15    | ERROR_SCRIPT_FILE_CORRUPT        |
| 16    | ERROR_SCRIPT_LINE_TOO_LONG       |
| 17    | ERROR_SCRIPT_UNKNOWN_OBJECT      |
| 18    | ERROR_SCRIPT_COUNT               |

### ANIM_GROUP_LIST (type 0xBB1E)

- Animation group enum, used for ANIM_GROUP parameters.

---

## Compiled Bytecode Format

### Top-Level Opcodes

```
[opcode:2] [paramLen:2] [data...]
```

All flow opcodes from PDB SCRIPT*OUTPUT enum (FLOW*\* section, values 16-31):

| Opcode  | PDB Name                | Data Format                                             |
| ------- | ----------------------- | ------------------------------------------------------- |
| 0x0010  | FLOW_BEGIN              | [modeLen:2] [mode:2] — mode is block type index         |
| 0x0011  | FLOW_END                | (no data)                                               |
| 0x0012  | FLOW_SHORT              | Variable type declaration (short/int16) — skip paramLen |
| 0x0013  | FLOW_LONG               | Variable type declaration (long/int32) — skip paramLen  |
| 0x0014  | FLOW_FLOAT              | Variable type declaration (float) — skip paramLen       |
| 0x0015  | FLOW_SET                | [setLen:2] [variable] [exprLen:2] [expression...]       |
| 0x0016  | FLOW_IF                 | [compLen:2] [jumpOffset:2] [exprLen:2] [expression...]  |
| 0x0017  | FLOW_ELSE               | [elseLen:2] (jump data)                                 |
| 0x0018  | FLOW_ELSEIF             | [elifLen:2] [jumpOffset:2] [exprLen:2] [expression...]  |
| 0x0019  | FLOW_ENDIF              | (no data)                                               |
| 0x001A  | FLOW_WHILE              | [compLen:2] [jumpOffset:2] [exprLen:2] [expression...]  |
| 0x001B  | FLOW_ENDWHILE           | (no data)                                               |
| 0x001C  | FLOW_REFERENCE_FUNCTION | [refIndex:2] — sets current reference for next call     |
| 0x001D  | FLOW_SCRIPTNAME         | (no data)                                               |
| 0x001E  | FLOW_RETURN             | (no data)                                               |
| 0x001F  | FLOW_REF                | Unknown — possibly reference-related declaration        |
| >=0x100 | FUNCTION\_\*            | [paramLen:2] [paramData...] — top-level function call   |

### Variable Encoding

```
Marker byte determines type:
  0x73 [index:2] — integer local variable
  0x66 [index:2] — float local variable
  0x72 [refIdx:2] [varMarker:1] [varIdx:2] — reference.variable (6 bytes)
  0x47 [index:2] — global variable
```

### Expression Token Format (RPN stack-based)

**Verification status**: These byte encodings are implementation constants embedded in machine code,
not in PDB type data. They are consistent with established Bethesda modding community documentation
(UESP, xEdit). Full verification requires disassembly of `ScriptRunner::EvaluateLine` ([0006:0x227C])
and `ScriptCompiler::GetNextExpression` ([0008:0x33F4]) in the game executable.
The operator values are the ASCII codes of the operator characters (0x26='&', 0x7C='|', etc.).
The variable markers use mnemonics: 0x73='s' (short/int), 0x66='f' (float), 0x72='r' (ref), 0x47='G' (global).

```
Tokens within expressions (if/set/elseif condition or value):
  0x20 [subtoken] — Push prefix (next byte determines value type)
  0x58 — Function call within expression (followed by function call format)
  0x6E [int32:4] — Integer literal (standalone, no push prefix)
  0x7A [double:8] — Double literal (standalone, no push prefix)
  0x72 [refIdx:2] — SCRO reference (standalone)
  0x73 [index:2] — Integer local variable (standalone)
  0x66 [index:2] — Float local variable (standalone)

Operators (single byte or two bytes):
  0x26 0x26 = &&    0x7C 0x7C = ||
  0x3D 0x3D = ==    0x21 0x3D = !=
  0x3E 0x3D = >=    0x3C 0x3D = <=
  0x3E = >    0x3C = <
  0x2B = +    0x2D = -
  0x2A = *    0x2F = /
```

### Function Call Parameter Format

#### Top-level function calls (opcode >= 0x100)

```
[opcode:2] [paramLen:2] [paramCount:2] [params...]
```

#### Expression function calls (after 0x58 marker)

```
[0x58] [opcode:2] [paramLen:2] [paramCount:2] [params...]
```

#### Individual Parameter Encoding — NEEDS EMPIRICAL VERIFICATION

Each parameter is encoded with a 2-byte type prefix:

```
[typeCode:2] [data...]
```

Where typeCode comes from SCRIPT_PARAM_TYPE enum, and data size depends on type.

The `pExecuteFunction` receives `const char* compiledParamData` and uses
`const SCRIPT_PARAMETER*` definitions to know what types to expect. But the
SCRIPT_PARAMETER_DEF.bCanBeVariable flag indicates parameters may also be
variable references (using 0x73/0x66/0x72 markers instead of literal data).

---

## Key Insights from PDB

1. **Script::PutNumericIDInDouble / GetNumericIDFromDouble**: ALL numeric IDs
   (FormIDs, actor values, etc.) are packed into IEEE 754 doubles in the engine.
   This means in compiled bytecode, FormID references may be stored as 8-byte
   doubles, not 4-byte uint32s.

2. **SCRIPT_PARAMETER_DEF.bCanBeVariable**: When true, a parameter's compiled
   data can be either a literal value OR a variable reference (0x73/0x66/0x72
   markers). The runtime must check which format is present.

3. **SCRIPT_PARAMETER_DEF.bReferencedObject**: When true, the parameter value
   is a FormID that gets resolved via the SCRO list. In compiled form, this
   is likely a 2-byte SCRO index (1-based).

4. **SCRIPT_LINE.pOutput**: Each source line compiles to up to 512 bytes of
   output. The compiled bytecode is the concatenation of all line outputs.

5. **ScriptRunner::EndianNextLine**: Byte-swaps one line of compiled bytecode.
   Its parameters (SCRIPT_OUTPUT&, uint&, int&) suggest it knows the opcode
   type, a uint value, and an int offset — and swaps fields accordingly.

6. **ScriptRunner::GetNextLine**: The bytecode reader. Takes Script\*,
   outputs SCRIPT_OUTPUT (opcode), uint (param data/offset), int (position),
   TESObjectREFR\*\* (ref target), and bool. This is the inverse of compilation.
