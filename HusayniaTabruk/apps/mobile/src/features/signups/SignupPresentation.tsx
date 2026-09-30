import { StyleSheet, Text, View } from "react-native";

import type { SignupResponse } from "../../core/api/auth-api";
import type { PendingSignupCommand } from "../../core/security/pending-signup-storage";
import { authTheme } from "../auth/auth-theme";
import { formatCategory } from "../dates/DateDetailScreen";

type SignupPresentationProps =
  | {
      readonly pendingCommand: PendingSignupCommand;
      readonly signup?: never;
    }
  | {
      readonly pendingCommand?: never;
      readonly signup: SignupResponse;
    };

export function SignupPresentation(props: SignupPresentationProps) {
  if (props.pendingCommand) {
    const command = props.pendingCommand;
    const total =
      1 +
      command.body.memberParticipantIds.length +
      command.body.unnamedParticipantCount;

    return (
      <View
        accessibilityLabel={`Signup for ${formatCategory(command.category)}, Pending sync`}
        style={styles.card}
      >
        <Text accessibilityRole="header" style={styles.title}>
          {formatCategory(command.category)}
        </Text>
        <Text style={styles.status}>Status: Pending sync</Text>
        <Text style={styles.body}>Composition: {formatKind(command.body.kind)}</Text>
        {command.body.label ? (
          <Text style={styles.body}>Generic label: {command.body.label}</Text>
        ) : null}
        <Text style={styles.body}>Total participants: {total}</Text>
        <Text style={styles.body}>
          Selected eligible members: {command.body.memberParticipantIds.length}
        </Text>
        <Text style={styles.body}>
          Unnamed participants: {command.body.unnamedParticipantCount}
        </Text>
      </View>
    );
  }

  const signup = props.signup;
  return (
    <View
      accessibilityLabel={`Signup for ${formatCategory(signup.category)}, ${formatStatus(signup.status)}`}
      style={styles.card}
    >
      <Text accessibilityRole="header" style={styles.title}>
        {formatCategory(signup.category)}
      </Text>
      <Text style={styles.status}>Status: {formatStatus(signup.status)}</Text>
      <Text style={styles.body}>Primary contact: {signup.primaryContact.displayName}</Text>
      <Text style={styles.body}>Composition: {formatKind(signup.kind)}</Text>
      {signup.label ? (
        <Text style={styles.body}>Generic label: {signup.label}</Text>
      ) : null}
      <Text style={styles.body}>
        Total participants: {signup.totalParticipantCount}
      </Text>
      {signup.memberParticipants.length > 0 ? (
        <View style={styles.participants}>
          <Text style={styles.body}>Eligible members:</Text>
          {signup.memberParticipants.map((participant) => (
            <Text key={participant.membershipId} style={styles.body}>
              {participant.displayName}
            </Text>
          ))}
        </View>
      ) : null}
      <Text style={styles.body}>
        Unnamed participants: {signup.unnamedParticipantCount}
      </Text>
    </View>
  );
}

export function formatStatus(status: SignupResponse["status"]): string {
  return `${status.charAt(0).toUpperCase()}${status.slice(1)}`;
}

function formatKind(kind: SignupResponse["kind"]): string {
  return `${kind.charAt(0).toUpperCase()}${kind.slice(1)}`;
}

const styles = StyleSheet.create({
  body: {
    color: authTheme.primaryText,
    fontSize: 15,
    lineHeight: 22,
  },
  card: {
    backgroundColor: authTheme.panel,
    borderColor: authTheme.border,
    borderRadius: 18,
    borderWidth: 1,
    gap: 7,
    padding: 17,
  },
  participants: {
    gap: 4,
  },
  status: {
    color: authTheme.accent,
    fontSize: 16,
    fontWeight: "800",
  },
  title: {
    color: authTheme.primaryText,
    fontSize: 19,
    fontWeight: "800",
  },
});
