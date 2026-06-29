# March Timer Templates

This directory contains template images used for detecting active march timers from AutoHunt and AutoIntel tasks.

## Required Templates

The following template images should be placed in this directory for optimal march timer detection:

### Visual Indicators
- `march_progress.png` - March progress bar image
- `march_timer_icon.png` - Timer icon that appears during marches
- `troops_marching.png` - Troops marching indicator
- `return_timer.png` - Return timer indicator
- `march_overlay.png` - March overlay/UI element

### Usage
These templates are automatically used by the MarchTimerService when checking for active march timers before farming operations.

### Image Requirements
- Images should be PNG format
- Captured from 720x1280 resolution
- Should show clear, distinctive visual elements
- Avoid including variable text/numbers in templates

### Detection Areas
- **MarchTimerArea**: Rectangle(10, 10, 700, 200) - Top area where march timers appear
- **TimerTextArea**: Rectangle(50, 50, 200, 30) - Area around timer text for OCR

## How It Works

1. **Visual Detection**: Templates are matched against screenshots using template matching
2. **OCR Detection**: Timer text is extracted using OCR to determine remaining time
3. **Cache**: Results are cached for 30 seconds to avoid excessive processing
4. **Integration**: Results are used by FarmingTask to determine whether to proceed or wait

## Troubleshooting

If march timer detection isn't working properly:

1. **Check template images**: Ensure templates match current game UI
2. **Verify resolution**: Templates should be captured at 720x1280
3. **Check logs**: Enable debug logging to see detection confidence scores
4. **Update areas**: Timer areas may need adjustment for different screen layouts

## Logging

The service provides detailed logging about:
- Template matching confidence scores  
- OCR text extraction results
- Cache hit/miss ratios
- Timer detection status

Enable debug logging to see detailed march timer detection information.