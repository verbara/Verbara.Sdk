#!/usr/bin/env bash
set -euo pipefail

JAVA_BASE="/home/harol/Repositories/Sources/asterisk-java/src/main/java/org/asteriskjava/manager"
DOTNET_BASE="/home/harol/Repositories/Sources/Asterisk.NetAot/src/Asterisk.NetAot.Ami"

ACTION_SRC="$JAVA_BASE/action"
EVENT_SRC="$JAVA_BASE/event"
ACTION_DST="$DOTNET_BASE/Actions"
EVENT_DST="$DOTNET_BASE/Events"

# Skip lists
ACTION_SKIP="ManagerAction|AbstractManagerAction|EventGeneratingAction|VariableInheritance"
EVENT_SKIP="ManagerEvent|AbstractAgentEvent|AbstractBridgeEvent|AbstractChannelEvent|AbstractChannelStateEvent|AbstractChannelTalkingEvent|AbstractConfbridgeEvent|AbstractFaxEvent|AbstractHoldEvent|AbstractMeetMeEvent|AbstractMixMonitorEvent|AbstractMonitorEvent|AbstractParkedCallEvent|AbstractQueueMemberEvent|AbstractRtcpEvent|AbstractRtpStatEvent|AbstractSecurityEvent|AbstractUnParkedEvent|ResponseEvent|UserEvent|ContactStatusEnum"

# Getter skip lists (inherited from base)
ACTION_GETTER_SKIP="getAction|getActionId|getAttributes|getClass"
EVENT_GETTER_SKIP="getPrivilege|getUniqueId|getTimestamp|getSource|getDateReceived|getClass|getChannel|getChannelState|getChannelStateDesc|getCallerIdNum|getCallerIdName|getConnectedLineNum|getConnectedLineName|getAccountCode|getContext|getExten|getPriority|getLanguage|getLinkedid|getBridgeUniqueid|getBridgeType|getBridgeTechnology|getBridgeCreator|getBridgeName|getBridgeNumChannels|getQueue|getMemberName|getInterface|getStateInterface|getMembership|getPenalty|getCallsTaken|getStatus|getPaused|getPausedReason|getRinginuse|getLastCall|getLastPause|getInCall|getAgent|getConference|getMeetme|getUsernum|getEventTV|getSeverity|getService|getAccountID|getSessionID|getLocalAddress|getRemoteAddress|getSessionTV|getLocalStationID|getRemoteStationID|getBridgeVideoSourceMode"

# Map Java type to C# type
map_type() {
    local jtype="$1"
    case "$jtype" in
        String) echo "string?" ;;
        Integer|int) echo "int?" ;;
        Long|long) echo "long?" ;;
        Boolean|boolean) echo "bool?" ;;
        Double|double) echo "double?" ;;
        Date) echo "DateTimeOffset?" ;;
        *) echo "" ;; # skip complex types
    esac
}

# Extract getters from a Java file, output: "Type PropertyName" per line
extract_properties() {
    local file="$1"
    local skip_pattern="$2"

    # Find public getter methods: public TYPE getXxx()
    grep -oP 'public\s+(\w+)\s+(get\w+)\s*\(' "$file" 2>/dev/null | \
    while IFS= read -r line; do
        local jtype prop_raw
        jtype=$(echo "$line" | sed -E 's/public\s+(\w+)\s+get.*/\1/')
        prop_raw=$(echo "$line" | sed -E 's/.*\s+(get(\w+))\s*\(.*/\2/')
        local getter_name
        getter_name=$(echo "$line" | sed -E 's/.*\s+(get\w+)\s*\(.*/\1/')

        # Skip inherited getters
        if echo "$getter_name" | grep -qE "^($skip_pattern)$"; then
            continue
        fi

        local cstype
        cstype=$(map_type "$jtype")
        if [ -n "$cstype" ] && [ -n "$prop_raw" ]; then
            echo "$cstype $prop_raw"
        fi
    done
}

# Determine event base class from Java extends clause
get_event_base() {
    local file="$1"
    local extends
    extends=$(grep -oP 'extends\s+(\w+)' "$file" | head -1 | awk '{print $2}')

    case "$extends" in
        AbstractChannelEvent|AbstractChannelStateEvent|AbstractChannelTalkingEvent|AbstractHoldEvent|AbstractMonitorEvent|AbstractMixMonitorEvent)
            echo "ChannelEventBase" ;;
        AbstractBridgeEvent) echo "BridgeEventBase" ;;
        AbstractQueueMemberEvent) echo "QueueMemberEventBase" ;;
        AbstractAgentEvent) echo "AgentEventBase" ;;
        AbstractConfbridgeEvent) echo "ConfbridgeEventBase" ;;
        AbstractMeetMeEvent) echo "MeetMeEventBase" ;;
        AbstractSecurityEvent) echo "SecurityEventBase" ;;
        AbstractFaxEvent) echo "FaxEventBase" ;;
        AbstractParkedCallEvent|AbstractUnParkedEvent) echo "ChannelEventBase" ;;
        AbstractRtcpEvent|AbstractRtpStatEvent) echo "ManagerEvent" ;;
        ResponseEvent) echo "ResponseEvent" ;;
        *) echo "ManagerEvent" ;;
    esac
}

# Check if action implements EventGeneratingAction
is_event_generating() {
    local file="$1"
    grep -q "EventGeneratingAction" "$file" 2>/dev/null && echo "true" || echo "false"
}

# Get the AsteriskMapping name from getAction() return value, or derive from class name
get_action_mapping() {
    local file="$1"
    local classname="$2"
    # Try to extract from: return "ActionName";
    local mapping
    mapping=$(grep -oP 'return\s+"(\w+)"' "$file" | head -1 | sed 's/return "//' | sed 's/"//')
    if [ -n "$mapping" ]; then
        echo "$mapping"
    else
        # Strip "Action" suffix
        echo "${classname%Action}"
    fi
}

# Generate Actions
echo "=== Generating Actions ==="
action_count=0
for java_file in "$ACTION_SRC"/*.java; do
    classname=$(basename "$java_file" .java)

    # Skip base/abstract classes
    if echo "$classname" | grep -qE "^($ACTION_SKIP)$"; then
        continue
    fi

    # Skip abstract classes
    if grep -q "abstract class" "$java_file"; then
        continue
    fi

    mapping=$(get_action_mapping "$java_file" "$classname")
    is_eg=$(is_event_generating "$java_file")

    # Build implements clause
    implements="ManagerAction"
    if [ "$is_eg" = "true" ]; then
        implements="ManagerAction, IEventGeneratingAction"
    fi

    # Extract properties
    props=$(extract_properties "$java_file" "$ACTION_GETTER_SKIP")

    # Generate C# file
    outfile="$ACTION_DST/${classname}.cs"
    {
        echo "using Asterisk.NetAot.Abstractions;"
        echo "using Asterisk.NetAot.Abstractions.Attributes;"
        echo ""
        echo "namespace Asterisk.NetAot.Ami.Actions;"
        echo ""
        echo "[AsteriskMapping(\"$mapping\")]"
        echo "public sealed class $classname : $implements"
        echo "{"
        if [ -n "$props" ]; then
            while IFS= read -r prop; do
                cstype=$(echo "$prop" | cut -d' ' -f1)
                propname=$(echo "$prop" | cut -d' ' -f2)
                echo "    public $cstype $propname { get; set; }"
            done <<< "$props"
        fi
        echo "}"
        echo ""
    } > "$outfile"

    action_count=$((action_count + 1))
done
echo "Generated $action_count action files"

# Generate Events
echo "=== Generating Events ==="
event_count=0
for java_file in "$EVENT_SRC"/*.java; do
    classname=$(basename "$java_file" .java)

    # Skip base/abstract classes
    if echo "$classname" | grep -qE "^($EVENT_SKIP)$"; then
        continue
    fi

    # Skip abstract classes
    if grep -q "abstract class" "$java_file"; then
        continue
    fi

    # Derive mapping name (strip "Event" suffix, or keep if no suffix)
    mapping="${classname%Event}"
    if [ "$mapping" = "$classname" ]; then
        # No Event suffix (e.g., ContactList, EndpointDetail)
        mapping="$classname"
    fi

    base=$(get_event_base "$java_file")

    # Determine which getter skip pattern to use based on base class
    skip_getters="$EVENT_GETTER_SKIP"

    # Extract properties
    props=$(extract_properties "$java_file" "$EVENT_GETTER_SKIP")

    # Determine if we need the Base using
    needs_base_using="false"
    case "$base" in
        ChannelEventBase|BridgeEventBase|QueueMemberEventBase|AgentEventBase|ConfbridgeEventBase|MeetMeEventBase|SecurityEventBase|FaxEventBase)
            needs_base_using="true" ;;
    esac

    needs_response_using="false"
    if [ "$base" = "ResponseEvent" ]; then
        needs_response_using="true"
    fi

    # Generate C# file
    outfile="$EVENT_DST/${classname}.cs"
    {
        echo "using Asterisk.NetAot.Abstractions;"
        echo "using Asterisk.NetAot.Abstractions.Attributes;"
        if [ "$needs_base_using" = "true" ]; then
            echo "using Asterisk.NetAot.Ami.Events.Base;"
        fi
        echo ""
        echo "namespace Asterisk.NetAot.Ami.Events;"
        echo ""
        echo "[AsteriskMapping(\"$mapping\")]"
        echo "public sealed class $classname : $base"
        echo "{"
        if [ -n "$props" ]; then
            while IFS= read -r prop; do
                cstype=$(echo "$prop" | cut -d' ' -f1)
                propname=$(echo "$prop" | cut -d' ' -f2)
                echo "    public $cstype $propname { get; set; }"
            done <<< "$props"
        fi
        echo "}"
        echo ""
    } > "$outfile"

    event_count=$((event_count + 1))
done
echo "Generated $event_count event files"
echo "=== Total: $((action_count + event_count)) files ==="
