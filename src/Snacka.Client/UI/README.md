# UI Components

This folder contains **shared presentational components** (also known as "dumb components" or "UI components").

## Philosophy

These components follow the presentational/container pattern:

- **Presentational (this folder)**: Focus purely on how things look. They receive data via properties, have no business logic, and are reusable across features.
- **Container (Controls folder)**: Feature-specific controls that compose UI components and connect them to ViewModels and business logic.

## Guidelines for UI Components

### 1. No Business Logic
UI components should not contain business logic, API calls, or complex state management. They only handle visual presentation and user interaction events.

```csharp
// GOOD: Exposes a command property for the parent to handle
public static readonly StyledProperty<ICommand?> CommandProperty = ...

// BAD: Contains business logic
private async void OnClick() {
    await _apiClient.DoSomething(); // Don't do this
}
```

### 2. Configurable via Properties
All visual aspects should be configurable through styled properties:

```csharp
public static readonly StyledProperty<string> PrimaryTextProperty = ...
public static readonly StyledProperty<IBrush> BackgroundProperty = ...
```

### 3. Use Content Slots for Flexibility
For complex content areas, use content properties that accept any Control:

```xml
<ui:ListItemView>
    <ui:ListItemView.IconContent>
        <Border Background="...">...</Border>
    </ui:ListItemView.IconContent>
</ui:ListItemView>
```

### 4. Consistent Styling
UI components define their own consistent styling (padding, margins, colors) so that all usages look the same automatically.

## Available Components

### ListItemView
A reusable list item with icon, primary/secondary text, and optional right content.

```xml
<ui:ListItemView
    PrimaryText="Username"
    SecondaryText="Online"
    Command="{Binding SelectCommand}"
    CommandParameter="{Binding}">
    <ui:ListItemView.IconContent>
        <!-- Avatar or icon -->
    </ui:ListItemView.IconContent>
    <ui:ListItemView.RightContent>
        <!-- Badge, time, button, etc. -->
    </ui:ListItemView.RightContent>
</ui:ListItemView>
```

## Adding New Components

When you find yourself duplicating UI patterns across multiple controls:

1. Extract the common pattern into a new UI component in this folder
2. Make it configurable via properties
3. Update existing controls to use the shared component
4. Add documentation to this README

This ensures visual consistency and reduces maintenance burden.
