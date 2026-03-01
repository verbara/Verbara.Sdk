#!/usr/bin/env bash
# generate-actions.sh
# Generates C# POCO files for AMI Actions from Java source files.
# Usage: bash tools/generate-actions.sh
set -euo pipefail

JAVA_DIR="/home/harol/Repositories/Sources/asterisk-java/src/main/java/org/asteriskjava/manager/action"
OUT_DIR="/home/harol/Repositories/Sources/Asterisk.NetAot/src/Asterisk.NetAot.Ami/Actions"

# Files to skip (base/interface types already handled in C#)
SKIP_FILES="ManagerAction.java AbstractManagerAction.java EventGeneratingAction.java VariableInheritance.java"

# Getters to skip (inherited from base)
SKIP_GETTERS="getActionId getAction getActionCompleteEventClass"

mkdir -p "$OUT_DIR"

count=0

# Map a Java type to a C# type. Returns empty string for types we skip (Map, List, etc.)
map_type() {
    local jtype="$1"
    case "$jtype" in
        String)       echo "string?" ;;
        Integer|int)  echo "int?" ;;
        Long|long)    echo "long?" ;;
        Boolean|boolean) echo "bool?" ;;
        Double|double)   echo "double?" ;;
        Character|char)  echo "char?" ;;
        Map*|List*|Set*|Collection*) echo "" ;;  # skip complex types
        Class*)       echo "" ;;  # skip Class<?>
        *)            echo "" ;;  # skip unknown types
    esac
}

# Extract property name from getter method name: getChannel -> Channel
getter_to_property() {
    local getter="$1"
    # Remove "get" prefix
    echo "${getter#get}"
}

for javafile in "$JAVA_DIR"/*.java; do
    filename=$(basename "$javafile")

    # Skip non-Java files
    [[ "$filename" != *.java ]] && continue
    # Skip package.html
    [[ "$filename" == "package.html" ]] && continue

    # Skip explicitly excluded files
    skip=false
    for sf in $SKIP_FILES; do
        if [[ "$filename" == "$sf" ]]; then
            skip=true
            break
        fi
    done
    $skip && continue

    # Read the entire file content
    filecontent=$(<"$javafile")

    # Skip abstract classes
    if echo "$filecontent" | grep -q "public abstract class"; then
        continue
    fi

    # Skip interfaces (already checked EventGeneratingAction but be safe)
    if echo "$filecontent" | grep -q "public interface"; then
        continue
    fi

    # Skip enums
    if echo "$filecontent" | grep -q "public enum"; then
        continue
    fi

    # Get the class name (without .java extension)
    classname="${filename%.java}"

    # Determine the AsteriskMapping name (class name without "Action" suffix)
    if [[ "$classname" == *Action ]]; then
        mapping_name="${classname%Action}"
    else
        mapping_name="$classname"
    fi

    # Detect if the class implements EventGeneratingAction
    # Handles both single-line and multi-line declarations
    implements_ega=false
    if echo "$filecontent" | tr '\n' ' ' | grep -qE "implements\s+EventGeneratingAction|implements[^{]*EventGeneratingAction"; then
        implements_ega=true
    fi

    # Build the inheritance clause
    if $implements_ega; then
        inheritance="ManagerAction, IEventGeneratingAction"
    else
        inheritance="ManagerAction"
    fi

    # -------------------------------------------------------------------
    # Collect all getters from the file (own + getters from parent abstract
    # classes that are NOT AbstractManagerAction).
    # We merge them by also scanning the parent abstract class if present.
    # -------------------------------------------------------------------

    # Determine the parent class
    parent_class=$(echo "$filecontent" | tr '\n' ' ' | grep -oP 'extends\s+\K[A-Za-z0-9_]+' | head -1)

    # Gather getter lines from the Java file itself
    all_getter_content="$filecontent"

    # If parent is an abstract class other than AbstractManagerAction, also read its getters
    if [[ -n "$parent_class" && "$parent_class" != "AbstractManagerAction" ]]; then
        parent_file="$JAVA_DIR/${parent_class}.java"
        if [[ -f "$parent_file" ]]; then
            parent_content=$(<"$parent_file")
            all_getter_content="$all_getter_content
$parent_content"
        fi
    fi

    # Extract public getter methods: "public <Type> get<Name>()"
    # We look for patterns like: public String getChannel()
    # Handle multiline by collapsing to single line first
    collapsed=$(echo "$all_getter_content" | tr '\n' ' ' | sed 's/  */ /g')

    # Extract all getter signatures: public <ReturnType> get<Name>()
    # Use grep -oP to find them
    properties=""
    while IFS= read -r match; do
        [[ -z "$match" ]] && continue

        # Parse return type and method name
        # match looks like: "public String getChannel()" or "public Integer getPriority()"
        ret_type=$(echo "$match" | sed -E 's/public\s+([A-Za-z0-9_<>,? ]+)\s+get[A-Za-z0-9_]+\s*\(.*/\1/' | sed 's/^ *//;s/ *$//')
        method_name=$(echo "$match" | grep -oP 'get[A-Za-z0-9_]+(?=\s*\()' | head -1)

        [[ -z "$method_name" ]] && continue
        [[ -z "$ret_type" ]] && continue

        # Skip base class getters
        skip_getter=false
        for sg in $SKIP_GETTERS; do
            if [[ "$method_name" == "$sg" ]]; then
                skip_getter=true
                break
            fi
        done
        $skip_getter && continue

        # Map the Java type to C#
        # Clean up the return type (remove generics like Map<String, String>)
        base_type=$(echo "$ret_type" | sed 's/<.*//')
        csharp_type=$(map_type "$base_type")

        # Skip if type mapping returned empty (Map, List, etc.)
        [[ -z "$csharp_type" ]] && continue

        # Get property name
        prop_name=$(getter_to_property "$method_name")

        # Avoid duplicate properties
        if echo "$properties" | grep -q "    public $csharp_type $prop_name { get; set; }"; then
            continue
        fi

        properties="${properties}    public ${csharp_type} ${prop_name} { get; set; }
"
    done < <(echo "$collapsed" | grep -oP 'public\s+[A-Za-z0-9_<>,? ]+\s+get[A-Z][A-Za-z0-9_]*\s*\([^)]*\)' || true)

    # Remove trailing newline from properties
    properties=$(echo "$properties" | sed '/^$/d')

    # Build the C# file content
    csfile="$OUT_DIR/${classname}.cs"

    {
        echo "using Asterisk.NetAot.Abstractions;"
        echo "using Asterisk.NetAot.Abstractions.Attributes;"
        echo ""
        echo "namespace Asterisk.NetAot.Ami.Actions;"
        echo ""
        echo "[AsteriskMapping(\"${mapping_name}\")]"
        echo "public sealed class ${classname} : ${inheritance}"
        echo "{"
        if [[ -n "$properties" ]]; then
            echo "$properties"
        fi
        echo "}"
    } > "$csfile"

    count=$((count + 1))
done

echo "Generated $count C# action files in $OUT_DIR"
