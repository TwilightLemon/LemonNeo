# Desktop localization conventions

`LemonNeo.Desktop` uses `Irihi.Lingua` as its only UI localization system. The base
resource is `Resources/Languages/Strings.json`; culture-specific files use Lingua's
`Strings.<culture>.json` naming convention and are included as `AdditionalFiles`.

## Choose the appropriate Lingua API

### Static text in AXAML

Use Lingua's generated `Keys` and `Translate` markup extension. This is reactive and
compile-time checked, and it does not require a view-model property.

```xml
<TextBlock Text="{Translate {x:Static i18n:LanguageManager+Keys.Settings}}" />
```

Do not bind a view-model property that only returns one fixed localized resource.

### Formatted text in AXAML

Use `FormatTranslate` when the format arguments are available as bindings. Both the
active culture and the bound arguments then update the result automatically.

```xml
<TextBlock.Text>
    <FormatTranslate FormatKey="{x:Static i18n:LanguageManager+Keys.TrackCount}">
        <TranslateEntry Binding="{Binding TrackCount}" />
    </FormatTranslate>
</TextBlock.Text>
```

### Observable text exposed by a view model

When a view model must expose a localized value directly, expose the generated
observable and consume it with Avalonia's stream-binding operator.

```csharp
public IObservable<string?> Title => LanguageManager.Instance.Settings;
```

```xml
<TextBlock Text="{Binding Title^}" />
```

### Immediate strings required by .NET or platform APIs

Some APIs, such as file pickers, native menus, exceptions, and formatted domain
messages, require a string immediately. Use a generated `LinguaKey`, never a string
literal:

```csharp
var title = LanguageManager.GetCurrentValue(LanguageManager.Keys.ChooseFolder);
var message = _text.Format(LanguageManager.Keys.TrackSummary, count, duration);
```

The string indexer and string `Format` overload on `LocalizationService` are reserved
for keys genuinely selected at runtime, such as enum names, route names, or stored
status keys.

## Culture switching

Persist the user's `LanguagePreference` through the Desktop settings scope.
`LocalizationService` is the single bridge that maps that preference to a culture and
calls `LanguageManager.Instance.UpdateCulture`. Do not call `UpdateCulture` from views
or view models.

The service also updates `DefaultThreadCurrentCulture` and
`DefaultThreadCurrentUICulture`, so culture-sensitive formatting and comparisons stay
consistent with the selected UI language.

## Resource maintenance

When adding a resource:

1. Add the same key to `Strings.json` and every `Strings.<culture>.json` file.
2. Use string leaf values only; Lingua ignores JSON numbers, booleans, and `null`.
3. Rebuild the Desktop project so the source generator creates the key and observable.
4. Prefer a descriptive semantic key instead of embedding English wording in the key.
5. Keep format placeholders consistent across all translations.

When adding a culture, also update `LanguagePreference`, the culture mapping in
`LocalizationService`, and the language choices in the Settings view model and view.

## Review checklist

- No user-facing literal text when an existing resource key is appropriate.
- No `GetObservable("literal")` or `_text["literal"]`; use generated keys.
- No direct cast to `LinguaObservableString` outside `LanguageManager` and
  `LocalizationService`.
- Direct AXAML text uses `Translate`; bound formatted AXAML text uses
  `FormatTranslate`.
- Subscriptions and bindings are disposed with their owning view model or control.
- Both base and translated resource files contain the same keys.
