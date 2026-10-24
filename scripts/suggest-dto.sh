#!/bin/bash
#
# Spins up a disposable MariaDB container, applies embedded schema updates, then prints
# a suggested C# model for one table, following the conventions of RedShirt.Example.Api:
#
# * By default, a DTO for the Dapper source generator (see OrderDto in the API's DataStores.Order project).
# * With --entity-framework, an Entity Framework Core entity (see CustomerEntity in the API's
#   DataStores.Customer.Implementation project), followed by a commented-out OnModelCreating block.
#
# With --root-namespace, the namespace and using directives for the API's layout are written
# as well. Without it, only the class definition is printed.
#
# Warnings about things that need a developer's attention go to stderr.
#
# MariaDB-specific sections live in scripts/lib/disposable-db.sh. The type mapping in
# this script assumes MariaDB/MySQL column types.

set -euo pipefail

ROOT="$(readlink -f "$(dirname "${0}")/..")"
cd "${ROOT}"

# shellcheck source=scripts/lib/messages.sh
. "${ROOT}/scripts/lib/messages.sh"

usage() {
  echo "Usage: $(basename "${0}") <table> [--entity-framework] [--root-namespace <namespace>] [--class-name <name>] [-o output.cs]" >&2
}

TABLE_NAME=""
ROOT_NAMESPACE=""
CLASS_NAME=""
OUTPUT_PATH=""
ENTITY_FRAMEWORK=0
while [ $# -gt 0 ]; do
  case "${1}" in
    --entity-framework)
      ENTITY_FRAMEWORK=1
      ;;
    --root-namespace)
      shift
      ROOT_NAMESPACE="${1:-}"
      if [ -z "${ROOT_NAMESPACE}" ]; then
        error "Missing namespace after --root-namespace"
        exit 1
      fi
      ;;
    --class-name)
      shift
      CLASS_NAME="${1:-}"
      if [ -z "${CLASS_NAME}" ]; then
        error "Missing name after --class-name"
        exit 1
      fi
      ;;
    -o)
      shift
      OUTPUT_PATH="${1:-}"
      if [ -z "${OUTPUT_PATH}" ]; then
        error "Missing path after -o"
        exit 1
      fi
      ;;
    -h | --help)
      usage
      echo "  Writes a suggested C# model for <table> to stdout, or to output.cs when -o is set." >&2
      echo "  --entity-framework  Write an Entity Framework Core entity and suggested OnModelCreating configuration" >&2
      echo "                      instead of a DTO for the Dapper source generator." >&2
      echo "  --root-namespace    API root namespace (e.g. RedShirt.Example.Api). Adds namespace and using directives" >&2
      echo "                      following the conventions of the RedShirt.Example.Api project." >&2
      echo "  --class-name        Class name to use instead of <Table>Dto or <Table>Entity" >&2
      echo "                      (e.g. CustomerDto for the Customers table)." >&2
      exit 0
      ;;
    -*)
      error "Unknown argument: ${1}"
      exit 1
      ;;
    *)
      if [ -n "${TABLE_NAME}" ]; then
        error "Only one table may be given"
        exit 1
      fi
      TABLE_NAME="${1}"
      ;;
  esac
  shift
done

if [ -z "${TABLE_NAME}" ]; then
  usage
  exit 1
fi

# C# namespace: dotted identifiers (letter or underscore, then letters, digits, or underscores).
if [ -n "${ROOT_NAMESPACE}" ] && ! [[ "${ROOT_NAMESPACE}" =~ ^[A-Za-z_][A-Za-z0-9_]*(\.[A-Za-z_][A-Za-z0-9_]*)*$ ]]; then
  error "Root namespace must look like a C# namespace (e.g. RedShirt.Example.Api)."
  exit 1
fi

if [ -n "${CLASS_NAME}" ] && ! [[ "${CLASS_NAME}" =~ ^[A-Za-z_][A-Za-z0-9_]*$ ]]; then
  error "Class name must be a C# identifier (e.g. CustomerDto)."
  exit 1
fi

# shellcheck source=scripts/lib/disposable-db.sh
. "${ROOT}/scripts/lib/disposable-db.sh"

disposable_db_start

quoted_table="$(db_quote "${TABLE_NAME}")"

columns="$(db_query "SELECT COLUMN_NAME, DATA_TYPE, COLUMN_TYPE, IS_NULLABLE, COLUMN_KEY,
    CHARACTER_MAXIMUM_LENGTH, NUMERIC_PRECISION, NUMERIC_SCALE, CHARACTER_SET_NAME
  FROM information_schema.COLUMNS
  WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = '${quoted_table}'
  ORDER BY ORDINAL_POSITION")"

if [ -z "${columns}" ]; then
  error "Table not found in applied schema: ${TABLE_NAME} (see scripts/list-tables.sh)"
  exit 1
fi

# Secondary indexes are only needed for the Entity Framework configuration block.
indexes=""
if [ "${ENTITY_FRAMEWORK}" -eq 1 ]; then
  indexes="$(db_query "SELECT INDEX_NAME, NON_UNIQUE, COLUMN_NAME
    FROM information_schema.STATISTICS
    WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = '${quoted_table}' AND INDEX_NAME <> 'PRIMARY'
    ORDER BY INDEX_NAME, SEQ_IN_INDEX")"
fi

# Columns arrive as tab-separated rows: name, data type, full column type, nullable (YES/NO), key (PRI/UNI/MUL/empty),
# character length, numeric precision, numeric scale, character set. Missing values arrive as NULL.
# Indexes arrive through the INDEXES environment variable as tab-separated rows: index name, non-unique (0/1), column.
generate_model() {
  TABLE_NAME="${TABLE_NAME}" ROOT_NAMESPACE="${ROOT_NAMESPACE}" CLASS_NAME="${CLASS_NAME}" \
    ENTITY_FRAMEWORK="${ENTITY_FRAMEWORK}" INDEXES="${indexes}" \
    YELLOW="${YELLOW}" NC="${NC}" awk -F '\t' '
    # Same format as warning() in scripts/lib/messages.sh.
    function warn(message) {
      printf "%sWarning%s: %s\n", ENVIRON["YELLOW"], ENVIRON["NC"], message > "/dev/stderr"
    }

    # Converts a database name such as "order_items" to a C# identifier such as "OrderItems".
    function pascal(name,    parts, count, i, result) {
      count = split(name, parts, /[^A-Za-z0-9]+/)
      result = ""
      for (i = 1; i <= count; i++) {
        if (parts[i] != "") {
          result = result toupper(substr(parts[i], 1, 1)) substr(parts[i], 2)
        }
      }
      if (result == "" || result ~ /^[0-9]/) {
        result = "_" result
      }
      return result
    }

    # MariaDB/MySQL column type to C# type.
    function csharp_type(dataType, columnType, column,    unsigned) {
      unsigned = columnType ~ / unsigned/
      if (dataType == "char" && columnType ~ /^char\(36\)/) return "Guid"
      if (dataType == "uuid") return "Guid"
      if (dataType ~ /^(char|varchar|tinytext|text|mediumtext|longtext|json|enum|set)$/) return "string"
      if (dataType ~ /^(datetime|timestamp|date)$/) return "DateTime"
      if (dataType == "time") return "TimeSpan"
      if (dataType == "decimal") return "decimal"
      if (dataType == "float") return "float"
      if (dataType == "double") return "double"
      if (dataType == "tinyint" && columnType ~ /^tinyint\(1\)/) return "bool"
      if (dataType == "tinyint") return unsigned ? "byte" : "sbyte"
      if (dataType == "smallint") return unsigned ? "ushort" : "short"
      if (dataType ~ /^(mediumint|int)$/) return unsigned ? "uint" : "int"
      if (dataType == "bigint") return unsigned ? "ulong" : "long"
      if (dataType == "bit" && columnType == "bit(1)") return "bool"
      if (dataType == "bit") return "ulong"
      if (dataType == "year") return "short"
      if (dataType ~ /^(binary|varbinary|tinyblob|blob|mediumblob|longblob)$/) return "byte[]"
      warn("No C# type mapping for column " column " (" columnType "), using object.")
      return "object"
    }

    function csharp_string(value) {
      gsub(/\\/, "\\\\", value)
      gsub(/"/, "\\\"", value)
      return "\"" value "\""
    }

    # Lambda selecting one or more properties, e.g. "e => e.Id" or "e => new { e.A, e.B }".
    function selector(list,    parts, count, i, result) {
      count = split(list, parts, ",")
      if (count == 1) {
        return "e => e." parts[1]
      }
      result = "e => new { "
      for (i = 1; i <= count; i++) {
        result = result (i > 1 ? ", " : "") "e." parts[i]
      }
      return result " }"
    }

    # Fluent configuration calls for one property, joined with ".". Empty when nothing needs configuring.
    function property_configuration(i,    calls) {
      calls = ""
      if (properties[i] != names[i]) {
        calls = calls ".HasColumnName(" csharp_string(names[i]) ")"
      }
      if (types[i] == "string") {
        if (dataTypes[i] ~ /^(char|varchar)$/ && lengths[i] != "NULL") {
          calls = calls ".HasMaxLength(" lengths[i] ")"
        }
        if (dataTypes[i] == "char") {
          calls = calls ".IsFixedLength()"
        }
        if (charsets[i] == "ascii") {
          calls = calls ".IsUnicode(false)"
        }
        if (!nullable[i]) {
          calls = calls ".IsRequired()"
        }
      }
      if (types[i] == "decimal" && precisions[i] != "NULL") {
        calls = calls ".HasPrecision(" precisions[i] ", " scales[i] ")"
      }
      return calls
    }

    {
      count++
      names[count] = $1
      dataTypes[count] = $2
      properties[count] = pascal($1)
      types[count] = csharp_type($2, $3, $1)
      nullable[count] = ($4 == "YES")
      lengths[count] = $6
      precisions[count] = $7
      scales[count] = $8
      charsets[count] = $9
      propertyByColumn[$1] = properties[count]
      if ($5 == "PRI") {
        keyCount++
        keyIndex = count
        keyProperties = keyProperties (keyCount > 1 ? "," : "") properties[count]
      }
    }

    END {
      table = ENVIRON["TABLE_NAME"]
      rootNamespace = ENVIRON["ROOT_NAMESPACE"]
      className = ENVIRON["CLASS_NAME"]
      entityFramework = ENVIRON["ENTITY_FRAMEWORK"] == "1"
      if (className == "") {
        className = pascal(table) (entityFramework ? "Entity" : "Dto")
      }
      entityName = className
      sub(/(Dto|Entity)$/, "", entityName)
      if (entityName == "") {
        entityName = className
      }

      for (i = 1; i <= count; i++) {
        if (types[i] != "DateTime") {
          continue
        }
        if (properties[i] ~ /^(CreatedAt|DateCreated)(Utc)?$/ && !createdIndex) {
          createdIndex = i
        } else if (properties[i] ~ /^(UpdatedAt|DateUpdated)(Utc)?$/ && !updatedIndex) {
          updatedIndex = i
        }
      }

      if (entityFramework) {
        if (keyCount == 0) {
          warn("Table " table " has no primary key. The configuration uses HasNoKey(), so the entity is read-only.")
        }
        if (createdIndex) {
          warn("Entity Framework does not set " properties[createdIndex] " automatically. Set it in code or with a SaveChanges interceptor.")
        }
        if (updatedIndex) {
          warn("Entity Framework does not set " properties[updatedIndex] " automatically. Set it in code or with a SaveChanges interceptor.")
        }
      } else {
        if (keyCount == 1) {
          attributes[keyIndex] = "DbKey"
          if (types[keyIndex] != "Guid") {
            warn("Primary key " names[keyIndex] " is not a Guid. Generated POST calls only support Guid keys, consider [DoNotGeneratePost].")
          }
        } else if (keyCount == 0) {
          warn("Table " table " has no primary key. [DbKey] was not added.")
        } else {
          warn("Table " table " has a composite primary key. [DbKey] was not added.")
        }

        if (createdIndex && !(createdIndex in attributes)) {
          attributes[createdIndex] = "CreatedAtProperty"
        } else {
          warn("No created timestamp column (CreatedAtUtc or DateCreatedUtc) found. [CreatedAtProperty] was not added.")
        }
        if (updatedIndex && !(updatedIndex in attributes)) {
          attributes[updatedIndex] = "UpdatedAtProperty"
        } else {
          warn("No updated timestamp column (UpdatedAtUtc or DateUpdatedUtc) found. [UpdatedAtProperty] was not added.")
        }

        for (i = 1; i <= count; i++) {
          if (properties[i] != names[i]) {
            warn("Column " names[i] " was renamed to property " properties[i] " and will need a column mapping.")
          }
        }
      }

      if (rootNamespace != "") {
        if (entityFramework) {
          print "using System.ComponentModel.DataAnnotations.Schema;"
          print ""
          print "namespace " rootNamespace ".DataStores." entityName ".Implementation.Entities;"
        } else {
          print "using " rootNamespace ".DataStores.Analyzers.Abstractions.Attributes;"
          print "using " rootNamespace ".DataStores.Constants;"
          print ""
          print "namespace " rootNamespace ".DataStores." entityName ".Models;"
        }
        print ""
      }

      if (entityFramework) {
        print "[Table(" csharp_string(table) ")]"
        print "internal sealed class " className
      } else {
        print "[DbTable(" csharp_string(table) ", DatabaseConstants.PrimaryDatabaseConnectionStringName)]"
        print "public sealed class " className
      }
      print "{"
      for (i = 1; i <= count; i++) {
        # Attributed properties are set apart from their neighbours by a blank line.
        if (i > 1 && ((i in attributes) || ((i - 1) in attributes))) {
          print ""
        }
        if (i in attributes) {
          print "    [" attributes[i] "]"
        }
        if (entityFramework) {
          # Entity Framework updates tracked entities in place, so properties are settable.
          # Nullable columns are optional and are not marked required.
          print "    public " (nullable[i] ? "" : "required ") types[i] (nullable[i] ? "?" : "") " " properties[i] " { get; set; }"
        } else {
          print "    public required " types[i] (nullable[i] ? "?" : "") " " properties[i] " { get; init; }"
        }
      }
      print "}"

      if (!entityFramework) {
        exit
      }

      # Suggested configuration is commented out so that the output still compiles as a single file.
      print ""
      print "// Suggested configuration for OnModelCreating in the store'"'"'s DbContext:"
      print "//"
      print "// modelBuilder.Entity<" className ">(entity =>"
      print "// {"
      if (keyCount == 0) {
        print "//     entity.HasNoKey();"
      } else {
        print "//     entity.HasKey(" selector(keyProperties) ");"
      }
      for (i = 1; i <= count; i++) {
        calls = property_configuration(i)
        if (calls == "") {
          continue
        }
        callCount = gsub(/\)\./, ")\n//         .", calls)
        if (callCount >= 2) {
          # Long chains are split one call per line.
          print "//     entity.Property(e => e." properties[i] ")\n//         " calls ";"
        } else {
          gsub(/\n\/\/         /, "", calls)
          print "//     entity.Property(e => e." properties[i] ")" calls ";"
        }
      }

      indexCount = split(ENVIRON["INDEXES"], indexRows, "\n")
      for (i = 1; i <= indexCount; i++) {
        if (split(indexRows[i], indexFields, "\t") < 3) {
          continue
        }
        if (!(indexFields[1] in indexColumns)) {
          indexOrder[++indexNameCount] = indexFields[1]
          indexColumns[indexFields[1]] = propertyByColumn[indexFields[3]]
          indexUnique[indexFields[1]] = indexFields[2] == "0"
        } else {
          indexColumns[indexFields[1]] = indexColumns[indexFields[1]] "," propertyByColumn[indexFields[3]]
        }
      }
      for (i = 1; i <= indexNameCount; i++) {
        indexName = indexOrder[i]
        print "//     entity.HasIndex(" selector(indexColumns[indexName]) ")" (indexUnique[indexName] ? ".IsUnique()" : "") ";"
      }
      print "// });"
    }
  '
}

if [ -n "${OUTPUT_PATH}" ]; then
  generate_model <<<"${columns}" >"${OUTPUT_PATH}"
  success "Wrote ${OUTPUT_PATH}"
else
  generate_model <<<"${columns}"
fi
