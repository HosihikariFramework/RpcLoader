package hosihikari;

import java.util.ArrayList;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;

final class Json {
    private Json() {
    }

    static Object parse(String text) {
        return new Parser(text).parse();
    }

    static String stringify(Object value) {
        StringBuilder builder = new StringBuilder();
        write(value, builder);
        return builder.toString();
    }

    private static void write(Object value, StringBuilder builder) {
        if (value == null) {
            builder.append("null");
        } else if (value instanceof String text) {
            writeString(text, builder);
        } else if (value instanceof Boolean || value instanceof Number) {
            builder.append(value);
        } else if (value instanceof Map<?, ?> map) {
            builder.append('{');
            boolean first = true;
            for (Map.Entry<?, ?> entry : map.entrySet()) {
                if (!first) {
                    builder.append(',');
                }
                first = false;
                writeString(String.valueOf(entry.getKey()), builder);
                builder.append(':');
                write(entry.getValue(), builder);
            }
            builder.append('}');
        } else if (value instanceof Iterable<?> items) {
            builder.append('[');
            boolean first = true;
            for (Object item : items) {
                if (!first) {
                    builder.append(',');
                }
                first = false;
                write(item, builder);
            }
            builder.append(']');
        } else {
            writeString(String.valueOf(value), builder);
        }
    }

    private static void writeString(String value, StringBuilder builder) {
        builder.append('"');
        for (int index = 0; index < value.length(); index++) {
            char character = value.charAt(index);
            switch (character) {
                case '"' -> builder.append("\\\"");
                case '\\' -> builder.append("\\\\");
                case '\b' -> builder.append("\\b");
                case '\f' -> builder.append("\\f");
                case '\n' -> builder.append("\\n");
                case '\r' -> builder.append("\\r");
                case '\t' -> builder.append("\\t");
                default -> {
                    if (character < 0x20) {
                        builder.append(String.format("\\u%04x", (int) character));
                    } else {
                        builder.append(character);
                    }
                }
            }
        }
        builder.append('"');
    }

    private static final class Parser {
        private final String text;
        private int index;

        Parser(String text) {
            this.text = text;
        }

        Object parse() {
            Object value = parseValue();
            skipWhitespace();
            if (index != text.length()) {
                throw new IllegalArgumentException("unexpected trailing JSON at " + index);
            }
            return value;
        }

        private Object parseValue() {
            skipWhitespace();
            if (index >= text.length()) {
                throw new IllegalArgumentException("unexpected end of JSON");
            }
            char character = text.charAt(index);
            return switch (character) {
                case '{' -> parseObject();
                case '[' -> parseArray();
                case '"' -> parseString();
                case 't' -> parseLiteral("true", Boolean.TRUE);
                case 'f' -> parseLiteral("false", Boolean.FALSE);
                case 'n' -> parseLiteral("null", null);
                default -> parseNumber();
            };
        }

        private Map<String, Object> parseObject() {
            expect('{');
            Map<String, Object> result = new LinkedHashMap<>();
            skipWhitespace();
            if (peek('}')) {
                index++;
                return result;
            }
            while (true) {
                skipWhitespace();
                String key = parseString();
                skipWhitespace();
                expect(':');
                result.put(key, parseValue());
                skipWhitespace();
                if (peek('}')) {
                    index++;
                    return result;
                }
                expect(',');
            }
        }

        private List<Object> parseArray() {
            expect('[');
            List<Object> result = new ArrayList<>();
            skipWhitespace();
            if (peek(']')) {
                index++;
                return result;
            }
            while (true) {
                result.add(parseValue());
                skipWhitespace();
                if (peek(']')) {
                    index++;
                    return result;
                }
                expect(',');
            }
        }

        private String parseString() {
            expect('"');
            StringBuilder builder = new StringBuilder();
            while (index < text.length()) {
                char character = text.charAt(index++);
                if (character == '"') {
                    return builder.toString();
                }
                if (character != '\\') {
                    builder.append(character);
                    continue;
                }
                if (index >= text.length()) {
                    throw new IllegalArgumentException("unfinished escape");
                }
                char escape = text.charAt(index++);
                switch (escape) {
                    case '"' -> builder.append('"');
                    case '\\' -> builder.append('\\');
                    case '/' -> builder.append('/');
                    case 'b' -> builder.append('\b');
                    case 'f' -> builder.append('\f');
                    case 'n' -> builder.append('\n');
                    case 'r' -> builder.append('\r');
                    case 't' -> builder.append('\t');
                    case 'u' -> {
                        if (index + 4 > text.length()) {
                            throw new IllegalArgumentException("unfinished unicode escape");
                        }
                        builder.append((char) Integer.parseInt(text.substring(index, index + 4), 16));
                        index += 4;
                    }
                    default -> throw new IllegalArgumentException("bad escape \\" + escape);
                }
            }
            throw new IllegalArgumentException("unterminated string");
        }

        private Object parseNumber() {
            int start = index;
            while (index < text.length()) {
                char character = text.charAt(index);
                if ((character >= '0' && character <= '9') || character == '-' || character == '+'
                    || character == '.' || character == 'e' || character == 'E') {
                    index++;
                } else {
                    break;
                }
            }
            String number = text.substring(start, index);
            if (number.isEmpty()) {
                throw new IllegalArgumentException("expected a JSON value at " + start);
            }
            if (number.indexOf('.') >= 0 || number.indexOf('e') >= 0 || number.indexOf('E') >= 0) {
                return Double.parseDouble(number);
            }
            return Long.parseLong(number);
        }

        private Object parseLiteral(String literal, Object value) {
            if (!text.startsWith(literal, index)) {
                throw new IllegalArgumentException("expected " + literal + " at " + index);
            }
            index += literal.length();
            return value;
        }

        private void skipWhitespace() {
            while (index < text.length() && Character.isWhitespace(text.charAt(index))) {
                index++;
            }
        }

        private boolean peek(char character) {
            return index < text.length() && text.charAt(index) == character;
        }

        private void expect(char character) {
            if (!peek(character)) {
                throw new IllegalArgumentException("expected '" + character + "' at " + index);
            }
            index++;
        }
    }
}
