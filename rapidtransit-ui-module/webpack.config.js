const path = require("path");
const webpack = require("webpack");

module.exports = (_, argv) => {
  const mode = argv && argv.mode ? argv.mode : "production";
  const isDevelopment = mode === "development";

  return {
    mode,
    entry: path.resolve(__dirname, "src/index.tsx"),
    target: ["web", "es2020"],
    devtool: isDevelopment ? "source-map" : false,
    resolve: {
      extensions: [".ts", ".tsx", ".js"]
    },
    module: {
      rules: [
        {
          test: /\.tsx?$/,
          exclude: /node_modules/,
          use: {
            loader: "ts-loader"
          }
        }
      ]
    },
    experiments: {
      outputModule: true
    },
    externalsType: "window",
    externals: {
      react: "React",
      "react-dom": "ReactDOM",
      "cs2/modding": "cs2/modding",
      "cs2/api": "cs2/api",
      "cs2/l10n": "cs2/l10n",
      "cs2/ui": "cs2/ui"
    },
    plugins: [
      new webpack.DefinePlugin({
        __RT_UI_MODULE_DEV__: JSON.stringify(isDevelopment)
      })
    ],
    output: {
      path: path.resolve(__dirname, "dist"),
      filename: "RapidTransitMod.mjs",
      module: true,
      library: {
        type: "module"
      },
      clean: false
    }
  };
};
